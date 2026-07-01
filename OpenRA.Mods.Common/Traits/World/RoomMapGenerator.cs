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
	[Desc("A map generator that lays out rooms connected by corridors, with player rooms evenly spaced around a ring.")]
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

			[Desc("Hard safety cap on total room placement attempts.")]
			public readonly int RoomAttempts = default;

			[Desc("Stop placing rooms after this many consecutive failed attempts (packs the map as full as it will go).")]
			public readonly int MaxConsecutiveFailures = default;

			[Desc("Minimum gap (in cells) kept between two rooms so a wall always separates them.")]
			public readonly int RoomGap = default;

			[Desc("Radius of the ring that player rooms are evenly spaced around, as a percentage of the usable half-extent of the map.")]
			public readonly int SpawnRingRadiusPercent = default;

			[Desc("Attempts to resolve an overlap when nudging a player room into place on the spawn ring.")]
			public readonly int SpawnRoomAttempts = default;

			[Desc("Width (in cells) of carved corridors, randomized per corridor between these two values.")]
			public readonly int CorridorMinWidth = default;
			public readonly int CorridorMaxWidth = default;

			public readonly ushort FloorTile = default;
			public readonly ushort WallTile = default;

			[Desc("Solid backdrop tile filling all space outside rooms/corridors and their wall shell.")]
			public readonly ushort VoidTile = default;

			[Desc("Actor type for the ore driller (\"Ore mine\").")]
			public readonly string OreMineActor = default;

			[Desc("Actor type for the gem driller (\"Gem mine\").")]
			public readonly string GemMineActor = default;

			public readonly int SpawnMinimumRadius = default;
			public readonly int SpawnMaximumRadius = default;
			public readonly int SpawnZoneRadius = default;
			public readonly int SpawnCentralReservationFraction = default;

			public Parameters(MiniYaml my)
			{
				FieldLoader.Load(this, my);
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

			if (!terrainInfo.TryGetTerrainInfo(new TerrainTile(param.VoidTile, 0), out _))
				throw new MapGenerationException("Illegal VoidTile");

			// No mirror/rotation symmetry yet - see follow-up note in project memory.
			var terraformer = new Terraformer(args, map, modData, actorPlans, Symmetry.Mirror.None, 1);

			var random = new MersenneTwister(param.Seed);
			var layoutRandom = new MersenneTwister(random.Next());
			var pickAnyRandom = new MersenneTwister(random.Next());
			var spawnRandom = new MersenneTwister(random.Next());
			var drillerRandom = new MersenneTwister(random.Next());

			terraformer.InitMap();

			// Start fully solid with an impassable void backdrop; rooms/corridors get a wall
			// shell carved around them below, mirroring how classic RA interior missions render
			// black outside of walked space instead of tiling a wall texture everywhere.
			foreach (var mpos in map.AllCells.MapCoords)
				map.Tiles[mpos] = terraformer.PickTile(pickAnyRandom, param.VoidTile);

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

			// Place player rooms first, evenly spaced around a ring so every player has a
			// consistent distance to their neighbours (and to the map center). Everything else
			// is filled in afterwards by ordinary room generation - see project memory for why
			// there's no more a distinct "central battle room".
			var centerX = size.Width / 2;
			var centerY = size.Height / 2;
			var ringRadius = Math.Min(centerX - minX, centerY - minY) * param.SpawnRingRadiusPercent / 100;
			var angleOffset = layoutRandom.NextFloat() * 2 * MathF.PI;

			var rooms = new List<Room>();

			for (var p = 0; p < param.Players; p++)
			{
				var angle = angleOffset + p * (2 * MathF.PI / param.Players);
				var targetX = centerX + (int)(ringRadius * MathF.Cos(angle));
				var targetY = centerY + (int)(ringRadius * MathF.Sin(angle));

				Room? placed = null;
				for (var attempt = 0; attempt < param.SpawnRoomAttempts && placed == null; attempt++)
				{
					var w = layoutRandom.Next(param.MinRoomSize, param.MaxRoomSize + 1);
					var h = layoutRandom.Next(param.MinRoomSize, param.MaxRoomSize + 1);
					var jitter = attempt == 0 ? 0 : param.MaxRoomSize;
					var x = Math.Clamp(targetX - w / 2 + layoutRandom.Next(-jitter, jitter + 1), minX, maxX - w);
					var y = Math.Clamp(targetY - h / 2 + layoutRandom.Next(-jitter, jitter + 1), minY, maxY - h);

					var candidate = new Room(x, y, w, h);
					if (!rooms.Any(r => r.TooCloseTo(candidate, param.RoomGap)))
						placed = candidate;
				}

				rooms.Add(placed ?? throw new MapGenerationException("Not enough room for player spawns"));
			}

			// Keep placing ordinary rooms until the map won't take any more, rather than stopping
			// at a fixed attempt count - this packs the available space as full as it will go.
			var consecutiveFailures = 0;
			var totalAttempts = 0;
			while (consecutiveFailures < param.MaxConsecutiveFailures && totalAttempts < param.RoomAttempts)
			{
				totalAttempts++;

				var w = layoutRandom.Next(param.MinRoomSize, param.MaxRoomSize + 1);
				var h = layoutRandom.Next(param.MinRoomSize, param.MaxRoomSize + 1);
				if (maxX - w <= minX || maxY - h <= minY)
				{
					consecutiveFailures++;
					continue;
				}

				var candidate = new Room(
					layoutRandom.Next(minX, maxX - w),
					layoutRandom.Next(minY, maxY - h),
					w,
					h);

				if (rooms.Any(r => r.TooCloseTo(candidate, param.RoomGap)))
				{
					consecutiveFailures++;
					continue;
				}

				rooms.Add(candidate);
				consecutiveFailures = 0;
			}

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

					var corridorWidth = layoutRandom.Next(param.CorridorMinWidth, param.CorridorMaxWidth + 1);

					// Carve a corridor segment (always axis-aligned, since `corner` shares one
					// coordinate with both `from` and `to`) as a band `corridorWidth` cells thick.
					void CarveThickLine(CPos from, CPos to)
					{
						var half = (corridorWidth - 1) / 2;
						var extra = corridorWidth - 1 - half;
						if (from.Y == to.Y)
						{
							for (var offset = -half; offset <= extra; offset++)
							{
								var x = from.X;
								while (true)
								{
									CarveFloor(x, from.Y + offset);
									if (x == to.X)
										break;
									x += Math.Sign(to.X - x);
								}
							}
						}
						else
						{
							for (var offset = -half; offset <= extra; offset++)
							{
								var y = from.Y;
								while (true)
								{
									CarveFloor(from.X + offset, y);
									if (y == to.Y)
										break;
									y += Math.Sign(to.Y - y);
								}
							}
						}
					}

					CarveThickLine(a, corner);
					CarveThickLine(corner, b);

					connected.Add(bestTo);
					remaining.Remove(bestTo);
				}
			}

			// Carve a one-cell wall shell around every floor cell that borders the void, so
			// rooms/corridors read as walled rather than dissolving straight into black.
			{
				(int DX, int DY)[] neighbors =
				[
					(-1, -1), (0, -1), (1, -1),
					(-1, 0), (1, 0),
					(-1, 1), (0, 1), (1, 1),
				];

				foreach (var mpos in map.AllCells.MapCoords)
				{
					if (!floor[mpos])
						continue;

					var cpos = mpos.ToCPos(map.Grid.Type);
					foreach (var (dx, dy) in neighbors)
					{
						var neighborCPos = new CPos(cpos.X + dx, cpos.Y + dy);
						if (!map.Contains(neighborCPos))
							continue;

						var neighborMPos = neighborCPos.ToMPos(map);
						if (!floor[neighborMPos])
							map.Tiles[neighborMPos] = terraformer.PickTile(pickAnyRandom, param.WallTile);
					}
				}
			}

			// Rooms 0..Players-1 are the player rooms placed on the spawn ring above; they get
			// exactly one ore mine + one gem mine. Every other room gets either one gem mine or
			// two ore mines.
			// Only touch the room's own footprint, not the whole map - these run once per room,
			// and rooms.Count * map area quickly gets expensive on large maps.
			CellLayer<bool> RoomZoneable(Room room)
			{
				var zoneable = new CellLayer<bool>(map);
				zoneable.Clear(false);
				for (var y = room.Y; y < room.Y + room.H; y++)
					for (var x = room.X; x < room.X + room.W; x++)
					{
						var cpos = new CPos(x, y);
						if (map.Contains(cpos))
							zoneable[cpos.ToMPos(map)] = true;
					}

				terraformer.ZoneFromActors(zoneable, false);
				return zoneable;
			}

			// Placing 1-2 fixed-type actors directly within a known small room is cheap; the
			// general Terraformer.AddDistributedActors path scans the whole map per call, which
			// gets expensive multiplied over every room on a large map.
			void AddMines(Room room, string actorType, int count)
			{
				const int MinSpacingSquared = 9; // 3 cells apart
				var placed = new List<CPos>();
				for (var n = 0; n < count; n++)
				{
					CPos? chosen = null;
					for (var attempt = 0; attempt < 30 && chosen == null; attempt++)
					{
						var x = drillerRandom.Next(room.X + 1, room.X + room.W - 1);
						var y = drillerRandom.Next(room.Y + 1, room.Y + room.H - 1);
						var candidate = new CPos(x, y);
						if (!map.Contains(candidate))
							continue;
						if (placed.Any(p => (p - candidate).LengthSquared < MinSpacingSquared))
							continue;
						chosen = candidate;
					}

					if (chosen == null)
						continue;

					var actorPlan = new ActorPlan(map, actorType)
					{
						WPosCenterLocation = CellLayerUtils.CPosToWPos(chosen.Value, map.Grid.Type),
					};
					terraformer.ProjectPlaceDezoneActor(actorPlan);
					placed.Add(chosen.Value);
				}
			}

			for (var r = 0; r < rooms.Count; r++)
			{
				var room = rooms[r];
				var zoneable = RoomZoneable(room);

				if (r < param.Players)
				{
					var chosenCPos = terraformer.ChooseSpawnInZoneable(
						spawnRandom,
						zoneable,
						param.SpawnCentralReservationFraction,
						param.SpawnMinimumRadius,
						param.SpawnMaximumRadius,
						param.SpawnZoneRadius)
							?? throw new MapGenerationException("Not enough room for player spawns");

					var spawn = new ActorPlan(map, "mpspawn") { Location = chosenCPos };
					terraformer.ProjectPlaceDezoneActor(spawn, zoneable, new WDist(param.SpawnZoneRadius * 1024));

					AddMines(room, param.GemMineActor, 1);
					AddMines(room, param.OreMineActor, 1);
				}
				else
				{
					if (drillerRandom.Next(2) == 0)
						AddMines(room, param.GemMineActor, 1);
					else
						AddMines(room, param.OreMineActor, 2);
				}
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
