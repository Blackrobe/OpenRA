#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.MapGenerator;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.EditorWorld)]
	[Desc("A map generator that lays out rooms connected by corridors, with a central battle room.")]
	public sealed class RoomMapGeneratorInfo : TraitInfo, IEditorMapGeneratorInfo
	{
		[FieldLoader.Require]
		[Desc("Human-readable name this generator uses.")]
		[FluentReference]
		public readonly string Name = null;

		[FieldLoader.Require]
		[Desc("Internal id for this map generator.")]
		public readonly string Type = null;

		[FieldLoader.Require]
		[Desc("Tilesets that are compatible with this map generator.")]
		public readonly ImmutableArray<string> Tilesets = default;

		[FluentReference]
		[Desc("The title to use for generated maps.")]
		public readonly string MapTitle = "label-random-map";

		[Desc("The widget tree to open when the tool is selected.")]
		public readonly string PanelWidget = "MAP_GENERATOR_TOOL_PANEL";

		// This is purely of interest to the linter.
		[FieldLoader.LoadUsing(nameof(FluentReferencesLoader))]
		[FluentReference]
		public readonly ImmutableArray<string> FluentReferences = default;

		[FieldLoader.LoadUsing(nameof(SettingsLoader))]
		public readonly MiniYaml Settings;

		string IMapGeneratorInfo.Type => Type;
		string IMapGeneratorInfo.Name => Name;
		string IMapGeneratorInfo.MapTitle => MapTitle;

		static MiniYaml SettingsLoader(MiniYaml my)
		{
			return my.NodeWithKey("Settings").Value;
		}

		static object FluentReferencesLoader(MiniYaml my)
		{
			return new MapGeneratorSettings(null, my.NodeWithKey("Settings").Value)
				.Options.SelectMany(o => o.GetFluentReferences()).ToImmutableArray();
		}

		public IMapGeneratorSettings GetSettings()
		{
			return new MapGeneratorSettings(this, Settings);
		}

		/// <summary>A rectangular room, in map cell coordinates.</summary>
		readonly struct Room
		{
			public readonly int X, Y, W, H;

			public Room(int x, int y, int w, int h)
			{
				X = x;
				Y = y;
				W = w;
				H = h;
			}

			public CPos Center => new(X + W / 2, Y + H / 2);

			public int Area => W * H;

			public bool Contains(int x, int y)
			{
				return x >= X && x < X + W && y >= Y && y < Y + H;
			}

			/// <summary>Whether this room would sit within `gap` cells of another room.</summary>
			public bool TooCloseTo(Room other, int gap)
			{
				return X - gap < other.X + other.W &&
					X + W + gap > other.X &&
					Y - gap < other.Y + other.H &&
					Y + H + gap > other.Y;
			}
		}

		sealed class Parameters
		{
			public readonly int Seed = default;
			public readonly int Players = default;

			[Desc("Cells kept clear around the edge of the map.")]
			public readonly int Margin = default;

			public readonly int MinRoomSize = default;
			public readonly int MaxRoomSize = default;

			[Desc("Number of candidate rooms to try placing. Overlapping candidates are discarded.")]
			public readonly int RoomAttempts = default;

			[Desc("Minimum gap (in cells) kept between two rooms so a wall always separates them.")]
			public readonly int RoomGap = default;

			[Desc("Side length of the single central battle room.")]
			public readonly int BattleRoomSize = default;

			public readonly ushort FloorTile = default;
			public readonly ushort WallTile = default;

			[Desc("Room floor area (in cells) required per placed resource driller.")]
			public readonly int DrillerAreaDivisor = default;

			public readonly int SpawnMinimumRadius = default;
			public readonly int SpawnMaximumRadius = default;
			public readonly int SpawnZoneRadius = default;
			public readonly int SpawnCentralReservationFraction = default;

			[FieldLoader.LoadUsing(nameof(DrillerWeightsLoader))]
			public readonly IReadOnlyDictionary<string, int> DrillerWeights = default;

			public Parameters(MiniYaml my)
			{
				FieldLoader.Load(this, my);
			}

			static object DrillerWeightsLoader(MiniYaml my)
			{
				return my.NodeWithKey("DrillerWeights").Value.ToDictionary(subMy =>
				{
					if (Exts.TryParseInt32Invariant(subMy.Value, out var f))
						return f;
					else
						throw new YamlException($"Invalid driller weight `{subMy.Value}`");
				});
			}
		}

		public Map Generate(ModData modData, MapGenerationArgs args)
		{
			var terrainInfo = modData.DefaultTerrainInfo[args.Tileset];
			var size = args.Size;

			var map = new Map(modData, terrainInfo, size);
			var actorPlans = new List<ActorPlan>();

			var param = new Parameters(args.Settings);

			if (!terrainInfo.TryGetTerrainInfo(new TerrainTile(param.FloorTile, 0), out _))
				throw new MapGenerationException("Illegal FloorTile");

			if (!terrainInfo.TryGetTerrainInfo(new TerrainTile(param.WallTile, 0), out _))
				throw new MapGenerationException("Illegal WallTile");

			// No mirror/rotation symmetry yet - see follow-up note in project memory.
			var terraformer = new Terraformer(args, map, modData, actorPlans, Symmetry.Mirror.None, 1);

			var random = new MersenneTwister(param.Seed);
			var layoutRandom = new MersenneTwister(random.Next());
			var pickAnyRandom = new MersenneTwister(random.Next());
			var spawnRandom = new MersenneTwister(random.Next());
			var drillerRandom = new MersenneTwister(random.Next());

			terraformer.InitMap();

			// Start fully solid; rooms and corridors are carved out of it below.
			foreach (var mpos in map.AllCells.MapCoords)
				map.Tiles[mpos] = terraformer.PickTile(pickAnyRandom, param.WallTile);

			var floor = new CellLayer<bool>(map);

			void CarveFloor(int x, int y)
			{
				var cpos = new CPos(x, y);
				if (!map.Contains(cpos))
					return;
				var mpos = cpos.ToMPos(map);
				map.Tiles[mpos] = terraformer.PickTile(pickAnyRandom, param.FloorTile);
				floor[mpos] = true;
			}

			var minX = param.Margin;
			var minY = param.Margin;
			var maxX = size.Width - param.Margin;
			var maxY = size.Height - param.Margin;

			// Reserve the central battle room first, so every other room forms around it.
			var battleSize = param.BattleRoomSize;
			var battleRoom = new Room(
				(size.Width - battleSize) / 2,
				(size.Height - battleSize) / 2,
				battleSize,
				battleSize);

			var rooms = new List<Room> { battleRoom };

			for (var attempt = 0; attempt < param.RoomAttempts; attempt++)
			{
				var w = layoutRandom.Next(param.MinRoomSize, param.MaxRoomSize + 1);
				var h = layoutRandom.Next(param.MinRoomSize, param.MaxRoomSize + 1);
				if (maxX - w <= minX || maxY - h <= minY)
					continue;

				var candidate = new Room(
					layoutRandom.Next(minX, maxX - w),
					layoutRandom.Next(minY, maxY - h),
					w,
					h);

				if (rooms.Any(r => r.TooCloseTo(candidate, param.RoomGap)))
					continue;

				rooms.Add(candidate);
			}

			if (rooms.Count < param.Players + 1)
				throw new MapGenerationException("Not enough room for the interior layout");

			foreach (var room in rooms)
				for (var y = room.Y; y < room.Y + room.H; y++)
					for (var x = room.X; x < room.X + room.W; x++)
						CarveFloor(x, y);

			// Connect every room with a minimum-spanning-tree of L-shaped, one-cell-wide corridors.
			{
				var connected = new List<int> { 0 };
				var remaining = Enumerable.Range(1, rooms.Count - 1).ToList();
				while (remaining.Count > 0)
				{
					var bestFrom = 0;
					var bestTo = remaining[0];
					var bestDistSq = int.MaxValue;
					foreach (var i in connected)
						foreach (var j in remaining)
						{
							var d = (rooms[i].Center - rooms[j].Center).LengthSquared;
							if (d < bestDistSq)
							{
								bestDistSq = d;
								bestFrom = i;
								bestTo = j;
							}
						}

					var a = rooms[bestFrom].Center;
					var b = rooms[bestTo].Center;
					var corner = layoutRandom.Next(2) == 0 ? new CPos(b.X, a.Y) : new CPos(a.X, b.Y);

					void CarveLine(CPos from, CPos to)
					{
						var x = from.X;
						var y = from.Y;
						while (true)
						{
							CarveFloor(x, y);
							if (x == to.X && y == to.Y)
								break;

							if (x != to.X)
								x += Math.Sign(to.X - x);
							else
								y += Math.Sign(to.Y - y);
						}
					}

					CarveLine(a, corner);
					CarveLine(corner, b);

					connected.Add(bestTo);
					remaining.Remove(bestTo);
				}
			}

			// Player spawns: peripheral rooms only, never inside the central battle room.
			var spawnZoneable = new CellLayer<bool>(map);
			foreach (var mpos in map.AllCells.MapCoords)
			{
				var cpos = mpos.ToCPos(map.Grid.Type);
				spawnZoneable[mpos] = floor[mpos] && !battleRoom.Contains(cpos.X, cpos.Y);
			}

			for (var i = 0; i < param.Players; i++)
			{
				var chosenCPos = terraformer.ChooseSpawnInZoneable(
					spawnRandom,
					spawnZoneable,
					param.SpawnCentralReservationFraction,
					param.SpawnMinimumRadius,
					param.SpawnMaximumRadius,
					param.SpawnZoneRadius)
						?? throw new MapGenerationException("Not enough room for player spawns");

				var spawn = new ActorPlan(map, "mpspawn") { Location = chosenCPos };
				terraformer.ProjectPlaceDezoneActor(spawn, spawnZoneable, new WDist(param.SpawnZoneRadius * 1024));
			}

			// Resource driller clusters: skip the battle room (index 0), scale count by room area.
			for (var r = 1; r < rooms.Count; r++)
			{
				var room = rooms[r];
				var targetCount = Math.Max(1, room.Area / param.DrillerAreaDivisor);

				var roomZoneable = new CellLayer<bool>(map);
				foreach (var mpos in map.AllCells.MapCoords)
				{
					var cpos = mpos.ToCPos(map.Grid.Type);
					roomZoneable[mpos] = room.Contains(cpos.X, cpos.Y);
				}

				terraformer.ZoneFromActors(roomZoneable, false);

				var distribution = CellLayerUtils.Create(map, (MPos mpos) => roomZoneable[mpos] ? 1 : 0);

				terraformer.AddDistributedActors(
					drillerRandom,
					roomZoneable,
					distribution,
					param.DrillerWeights,
					targetCount,
					true);
			}

			terraformer.ReorderPlayerSpawns();
			terraformer.BakeMap();

			return map;
		}

		public bool TryGenerateMetadata(ModData modData, MapGenerationArgs args, out MapPlayers players, out Dictionary<string, MiniYaml> ruleDefinitions)
		{
			try
			{
				var playerCount = FieldLoader.GetValue<int>("Players", args.Settings.NodeWithKey("Players").Value.Value);

				// Generated maps use the default ruleset.
				ruleDefinitions = [];
				players = new MapPlayers(modData.DefaultRules, playerCount);

				return true;
			}
			catch
			{
				players = null;
				ruleDefinitions = null;
				return false;
			}
		}

		public override object Create(ActorInitializer init)
		{
			return new RoomMapGenerator(this);
		}

		ImmutableArray<string> IEditorMapGeneratorInfo.Tilesets => Tilesets;
	}

	public class RoomMapGenerator : IEditorTool
	{
		public string Label { get; }
		public string PanelWidget { get; }
		public TraitInfo TraitInfo { get; }
		public bool IsEnabled => true;

		public RoomMapGenerator(RoomMapGeneratorInfo info)
		{
			Label = info.Name;
			PanelWidget = info.PanelWidget;
			TraitInfo = info;
		}
	}
}
