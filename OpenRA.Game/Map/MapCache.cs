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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.FileSystem;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;
using FS = OpenRA.FileSystem.FileSystem;

namespace OpenRA
{
	enum MapCacheLoadPhase
	{
		MapFolders,
		ModRulesYaml,
		PreviewScan,
		PackageOpen,
		UidHash,
		PreviewUpdate,
		MapYaml,
		PreviewMetadata,
		PreviewRules,
		PreviewPng
	}

	sealed class MapCacheLoadMetrics
	{
		sealed class Measurement : IDisposable
		{
			readonly MapCacheLoadMetrics metrics;
			readonly MapCacheLoadPhase phase;
			readonly long start = Stopwatch.GetTimestamp();

			public Measurement(MapCacheLoadMetrics metrics, MapCacheLoadPhase phase)
			{
				this.metrics = metrics;
				this.phase = phase;
			}

			public void Dispose()
			{
				metrics.Add(phase, Stopwatch.GetTimestamp() - start);
			}
		}

		long mapFolderTicks;
		long modRulesYamlTicks;
		long previewScanTicks;
		long packageOpenTicks;
		long uidHashTicks;
		long previewUpdateTicks;
		long mapYamlTicks;
		long previewMetadataTicks;
		long previewRulesTicks;
		long previewPngTicks;
		int mapCount;
		int loadedMapCount;
		int failedMapCount;
		int nullPackageCount;
		int customRulesMapCount;
		int previewPngCount;
		int mapLocationCount;

		static float Milliseconds(long ticks) => 1000f * ticks / Stopwatch.Frequency;

		void Add(MapCacheLoadPhase phase, long ticks)
		{
			switch (phase)
			{
				case MapCacheLoadPhase.MapFolders: Interlocked.Add(ref mapFolderTicks, ticks); break;
				case MapCacheLoadPhase.ModRulesYaml: Interlocked.Add(ref modRulesYamlTicks, ticks); break;
				case MapCacheLoadPhase.PreviewScan: Interlocked.Add(ref previewScanTicks, ticks); break;
				case MapCacheLoadPhase.PackageOpen: Interlocked.Add(ref packageOpenTicks, ticks); break;
				case MapCacheLoadPhase.UidHash: Interlocked.Add(ref uidHashTicks, ticks); break;
				case MapCacheLoadPhase.PreviewUpdate: Interlocked.Add(ref previewUpdateTicks, ticks); break;
				case MapCacheLoadPhase.MapYaml: Interlocked.Add(ref mapYamlTicks, ticks); break;
				case MapCacheLoadPhase.PreviewMetadata: Interlocked.Add(ref previewMetadataTicks, ticks); break;
				case MapCacheLoadPhase.PreviewRules: Interlocked.Add(ref previewRulesTicks, ticks); break;
				case MapCacheLoadPhase.PreviewPng: Interlocked.Add(ref previewPngTicks, ticks); break;
			}
		}

		public IDisposable Measure(MapCacheLoadPhase phase) { return new Measurement(this, phase); }
		public void SetMapLocationCount(int count) { mapLocationCount = count; }
		public void AddMap() { Interlocked.Increment(ref mapCount); }
		public void AddLoadedMap() { Interlocked.Increment(ref loadedMapCount); }
		public void AddFailedMap() { Interlocked.Increment(ref failedMapCount); }
		public void AddNullPackage() { Interlocked.Increment(ref nullPackageCount); }
		public void AddCustomRulesMap() { Interlocked.Increment(ref customRulesMapCount); }
		public void AddPreviewPng() { Interlocked.Increment(ref previewPngCount); }

		public void Write(string operation)
		{
			var previewScan = Interlocked.Read(ref previewScanTicks);
			var packageOpen = Interlocked.Read(ref packageOpenTicks);
			var uidHash = Interlocked.Read(ref uidHashTicks);
			var previewUpdate = Interlocked.Read(ref previewUpdateTicks);
			var mapYaml = Interlocked.Read(ref mapYamlTicks);
			var previewMetadata = Interlocked.Read(ref previewMetadataTicks);
			var previewRules = Interlocked.Read(ref previewRulesTicks);
			var previewPng = Interlocked.Read(ref previewPngTicks);
			var scanUnattributed = Math.Max(0, previewScan - packageOpen - uidHash - previewUpdate);
			var updateUnattributed = Math.Max(0, previewUpdate - mapYaml - previewMetadata - previewRules - previewPng);

			Log.Write("perf",
				$"MapCache.{operation} breakdown: locations={mapLocationCount}, map-attempts={mapCount}, " +
				$"loaded={loadedMapCount}, failures={failedMapCount}, null-packages={nullPackageCount}, " +
				$"custom-rules={customRulesMapCount}, preview-pngs={previewPngCount}; " +
				$"folders={Milliseconds(Interlocked.Read(ref mapFolderTicks)):0.0} ms, " +
				$"mod-rules-yaml={Milliseconds(Interlocked.Read(ref modRulesYamlTicks)):0.0} ms, " +
				$"preview-scan-total={Milliseconds(previewScan):0.0} ms " +
				$"(package-open={Milliseconds(packageOpen):0.0} ms, uid-hash={Milliseconds(uidHash):0.0} ms, " +
				$"preview-update-total={Milliseconds(previewUpdate):0.0} ms, " +
				$"scan-unattributed={Milliseconds(scanUnattributed):0.0} ms); " +
				$"preview-update-detail=(map-yaml={Milliseconds(mapYaml):0.0} ms, " +
				$"metadata={Milliseconds(previewMetadata):0.0} ms, rules={Milliseconds(previewRules):0.0} ms, " +
				$"png={Milliseconds(previewPng):0.0} ms, unattributed={Milliseconds(updateUnattributed):0.0} ms)");
		}
	}

	public sealed class MapCache : IEnumerable<MapPreview>, IDisposable
	{
		[YamlNode("MapCache", shared: false)]
		sealed class MapCacheSettings : SettingsModule
		{
			public string LobbyMap = null;
			public string LobbyMapPath = null;
		}

		public static readonly MapPreview UnknownMap = new(null, null, MapGridType.Rectangular, null);
		public IReadOnlyDictionary<IReadOnlyPackage, MapClassification> MapLocations => mapLocations;
		readonly Dictionary<IReadOnlyPackage, MapClassification> mapLocations = [];
		public bool LoadPreviewImages = true;

		readonly Manifest manifest;
		readonly FS modFiles;
		Cache<string, MapPreview> previews;
		readonly object previewsSync = new();
		readonly SheetBuilder sheetBuilder;
		Thread previewLoaderThread;
		bool previewLoaderThreadShutDown = true;
		readonly object syncRoot = new();
		readonly Queue<MapPreview> generateMinimap = [];

		public HashSet<string> StringPool { get; } = [];

		readonly List<MapDirectoryTracker> mapDirectoryTrackers = [];
		MapGridType mapGridType;
		MiniYamlNode[][] modDataRules;
		MapCacheSettings mapCacheSettings;
		bool mapLocationsInitialized;
		volatile bool mapScanComplete;
		volatile bool backgroundMapScanInProgress;
		CancellationTokenSource backgroundMapScanCancellation;
		Task backgroundMapScanTask;
		int backgroundMapScanGeneration;

		public bool IsMapScanComplete => mapScanComplete;
		public bool IsBackgroundMapScanInProgress => backgroundMapScanInProgress;
		public bool HasAvailableLobbyMap
		{
			get
			{
				lock (previewsSync)
					return previews != null && previews.Values.Any(IsAvailableLobbyMap);
			}
		}

		/// <summary>
		/// The most recently modified or loaded map at runtime.
		/// </summary>
		public string LastModifiedMap { get; private set; } = null;
		readonly Dictionary<string, string> mapUpdates = [];

		string lastLoadedLastModifiedMap;

		/// <summary>
		/// If LastModifiedMap was picked already, returns a null.
		/// </summary>
		public string PickLastModifiedMap(MapVisibility visibility)
		{
			UpdateMaps();
			var map = string.IsNullOrEmpty(LastModifiedMap) ? null : this[LastModifiedMap];
			if (map != null && map.Status == MapStatus.Available && map.Visibility.HasFlag(visibility) && lastLoadedLastModifiedMap != LastModifiedMap)
			{
				lastLoadedLastModifiedMap = LastModifiedMap;
				return lastLoadedLastModifiedMap;
			}

			return null;
		}

		public MapCache(Manifest manifest, FS modFiles)
		{
			this.manifest = manifest;
			this.modFiles = modFiles;
			sheetBuilder = new SheetBuilder(SheetType.BGRA, manifest.RendererConstants.MapPreviewSheetSize);
		}

		public void UpdateMaps()
		{
			if (backgroundMapScanInProgress)
				return;

			foreach (var tracker in mapDirectoryTrackers)
				tracker.UpdateMaps(this);
		}

		public void LoadMaps(ModData modData)
		{
			// Utility mod that does not support maps
			if (manifest.MapFolders.Count == 0)
			{
				mapScanComplete = true;
				return;
			}

			var metrics = new MapCacheLoadMetrics();
			InitializeMapLocations(modData, metrics);
			mapCacheSettings = modData.GetSettings<MapCacheSettings>();

			using (metrics.Measure(MapCacheLoadPhase.PreviewScan))
				foreach (var kv in MapLocations)
					foreach (var map in kv.Key.Contents)
						LoadMapInternal(map, kv.Key, kv.Value, null, mapGridType, modDataRules, metrics);

			mapScanComplete = true;
			metrics.Write("LoadMaps");

			// We only want to track maps in runtime, not at loadtime
			LastModifiedMap = null;
		}

		public void LoadShellmaps(ModData modData)
		{
			if (manifest.Shellmaps.Length == 0)
			{
				LoadMaps(modData);
				return;
			}

			var metrics = new MapCacheLoadMetrics();
			InitializeMapLocations(modData, metrics);
			mapCacheSettings = modData.GetSettings<MapCacheSettings>();

			var shellmaps = manifest.Shellmaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var loadedShellmaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			using (metrics.Measure(MapCacheLoadPhase.PreviewScan))
			{
				foreach (var kv in MapLocations)
				{
					if (!kv.Value.HasFlag(MapClassification.System))
						continue;

					foreach (var map in kv.Key.Contents)
					{
						if (!shellmaps.Contains(map))
							continue;

						if (!loadedShellmaps.Add(map))
							throw new InvalidDataException($"Shellmap `{map}` exists in more than one system map folder.");

						var preview = LoadMapInternal(map, kv.Key, kv.Value, null, mapGridType, modDataRules, metrics);
						if (preview == null || !preview.Visibility.HasFlag(MapVisibility.Shellmap))
							throw new InvalidDataException($"Configured shellmap `{map}` is unavailable or does not declare Shellmap visibility.");
					}
				}
			}

			var missing = shellmaps.Except(loadedShellmaps).ToArray();
			if (missing.Length > 0)
				throw new InvalidDataException($"Configured shellmaps were not found: {string.Join(", ", missing)}");

			using (metrics.Measure(MapCacheLoadPhase.PreviewScan))
				LoadRememberedLobbyMap(modData, metrics);

			mapScanComplete = false;
			metrics.Write("LoadShellmaps");
			LastModifiedMap = null;
		}

		void LoadRememberedLobbyMap(ModData modData, MapCacheLoadMetrics metrics)
		{
			if (string.IsNullOrEmpty(mapCacheSettings.LobbyMap) || string.IsNullOrEmpty(mapCacheSettings.LobbyMapPath))
				return;

			foreach (var kv in MapLocations)
			{
				var rememberedPath = mapCacheSettings.LobbyMapPath;
				var rememberedName = Path.GetFileName(rememberedPath);
				var map = kv.Key.Contents.FirstOrDefault(candidate =>
					string.Equals(candidate, rememberedPath, StringComparison.Ordinal) ||
					string.Equals(Path.GetFileName(candidate), rememberedName, StringComparison.Ordinal));
				if (map == null)
					continue;

				var preview = LoadMapPreviewInBackground(
					map, kv.Key, kv.Value, modData, metrics, new HashSet<string>());
				if (preview?.Uid == mapCacheSettings.LobbyMap && IsAvailableLobbyMap(preview))
				{
					GetPreview(preview.Uid).UpdateFromBackground(preview);
					Log.Write("debug", $"Loaded remembered lobby map `{preview.Title}` before opening the menu.");
					return;
				}
			}

			Log.Write("debug", $"Remembered lobby map `{mapCacheSettings.LobbyMapPath}` is no longer available.");
		}

		void InitializeMapLocations(ModData modData, MapCacheLoadMetrics metrics)
		{
			if (mapLocationsInitialized)
				return;

			mapGridType = modData.GetOrCreate<MapGrid>().Type;
			previews = new Cache<string, MapPreview>(uid => new MapPreview(modData, uid, mapGridType, this));

			using (metrics.Measure(MapCacheLoadPhase.MapFolders))
			{
				foreach (var kv in manifest.MapFolders)
				{
					var name = kv.Key;
					var classification = string.IsNullOrEmpty(kv.Value)
						? MapClassification.Unknown : Enum.Parse<MapClassification>(kv.Value);

					IReadOnlyPackage package;
					var optional = name.StartsWith('~');
					if (optional)
						name = name[1..];

					try
					{
						// HACK: If the path is inside the support directory then we may need to create it
						// Assume that the path is a directory if there is not an existing file with the same name
						var resolved = Platform.ResolvePath(name);
						if (resolved.StartsWith(Platform.SupportDir, StringComparison.Ordinal) && !File.Exists(resolved))
							Directory.CreateDirectory(resolved);

						package = modFiles.OpenPackage(name);
					}
					catch
					{
						if (optional)
							continue;

						throw;
					}

					mapLocations.Add(package, classification);
					mapDirectoryTrackers.Add(new MapDirectoryTracker(package, classification));
				}
			}

			metrics.SetMapLocationCount(mapLocations.Count);

			// PERF: Load the mod YAML once outside the loop, and reuse it when resolving each maps custom YAML.
			using (metrics.Measure(MapCacheLoadPhase.ModRulesYaml))
				modDataRules = modData.GetRulesYaml();

			mapLocationsInitialized = true;
		}

		public void StartBackgroundMapScan(ModData modData)
		{
			if (mapScanComplete || backgroundMapScanInProgress || manifest.Shellmaps.Length == 0)
				return;

			if (backgroundMapScanTask != null && !backgroundMapScanTask.IsCompleted)
				return;

			var cancellation = new CancellationTokenSource();
			backgroundMapScanCancellation = cancellation;
			var generation = ++backgroundMapScanGeneration;
			backgroundMapScanInProgress = true;
			Game.BeforeGameStart += CancelBackgroundMapScan;

			var locations = MapLocations.ToArray();
			var shellmaps = manifest.Shellmaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var preferredInitialMap = Game.Settings.Server.Map;
			var preferredMapAlreadyLoaded = !string.IsNullOrEmpty(preferredInitialMap) &&
				TryGetPreview(preferredInitialMap, out var preferredPreview) && IsAvailableLobbyMap(preferredPreview);
			backgroundMapScanTask = Task.Run(() =>
			{
				var metrics = new MapCacheLoadMetrics();
				metrics.SetMapLocationCount(locations.Length);
				var stringPool = new HashSet<string>();
				var loaded = new List<MapPreview>();
				var publishedPreferredMap = preferredMapAlreadyLoaded;

				try
				{
					using (metrics.Measure(MapCacheLoadPhase.PreviewScan))
					{
						foreach (var kv in locations)
						{
							foreach (var map in kv.Key.Contents)
							{
								cancellation.Token.ThrowIfCancellationRequested();
								if (kv.Value.HasFlag(MapClassification.System) && shellmaps.Contains(map))
									continue;

								var preview = LoadMapPreviewInBackground(
									map, kv.Key, kv.Value, modData, metrics, stringPool);
								if (preview != null)
								{
									loaded.Add(preview);

									var isPreferredMap = !publishedPreferredMap &&
										preview.Uid == preferredInitialMap && IsAvailableLobbyMap(preview);
									if (isPreferredMap)
									{
										publishedPreferredMap = true;
										Game.RunAfterTick(() => PublishBackgroundMapPreview(
											modData, generation, cancellation, preview));
									}
								}
							}
						}
					}

					metrics.Write("BackgroundMapScan");
					Game.RunAfterTick(() => PublishBackgroundMapScan(modData, generation, cancellation, loaded));
				}
				catch (OperationCanceledException)
				{
					Game.RunAfterTick(() => CleanupBackgroundMapScan(cancellation));
				}
				catch (Exception e)
				{
					Log.Write("debug", "Background map scan failed with error:");
					Log.Write("debug", e);
					Game.RunAfterTick(() => FailBackgroundMapScan(modData, generation, cancellation));
				}
			});
		}

		MapPreview LoadMapPreviewInBackground(string map, IReadOnlyPackage package, MapClassification classification,
			ModData modData, MapCacheLoadMetrics metrics, HashSet<string> stringPool)
		{
			IReadOnlyPackage mapPackage = null;
			MapPreview preview = null;
			metrics.AddMap();
			try
			{
				using (metrics.Measure(MapCacheLoadPhase.PackageOpen))
					mapPackage = package.OpenPackage(map, modFiles);

				if (mapPackage == null)
				{
					metrics.AddNullPackage();
					return null;
				}

				string uid;
				using (metrics.Measure(MapCacheLoadPhase.UidHash))
					uid = Map.ComputeUID(mapPackage);

				preview = new MapPreview(modData, uid, mapGridType, this);
				using (metrics.Measure(MapCacheLoadPhase.PreviewUpdate))
					preview.UpdateFromMapWithoutOwningPackage(
						mapPackage, package, classification, mapGridType, modDataRules, metrics, stringPool);

				mapPackage.Dispose();
				metrics.AddLoadedMap();
				return preview;
			}
			catch (Exception e)
			{
				metrics.AddFailedMap();
				mapPackage?.Dispose();
				Console.WriteLine($"Failed to load map: {map}");
				Console.WriteLine("Details:");
				Console.WriteLine(e);
				Log.Write("debug", $"Failed to load map: {map}");
				Log.Write("debug", "Details:");
				Log.Write("debug", e);
				return preview;
			}
		}

		void PublishBackgroundMapPreview(ModData modData, int generation,
			CancellationTokenSource cancellation, MapPreview preview)
		{
			if (generation != backgroundMapScanGeneration || cancellation.IsCancellationRequested ||
				!ReferenceEquals(Game.ModData, modData) || !backgroundMapScanInProgress)
				return;

			GetPreview(preview.Uid).UpdateFromBackground(preview);
			Log.Write("debug", $"Background map scan published preferred lobby map `{preview.Title}`.");
		}

		void PublishBackgroundMapScan(ModData modData, int generation,
			CancellationTokenSource cancellation, List<MapPreview> loaded)
		{
			if (generation != backgroundMapScanGeneration || cancellation.IsCancellationRequested || !ReferenceEquals(Game.ModData, modData))
			{
				CleanupBackgroundMapScan(cancellation);
				return;
			}

			foreach (var preview in loaded)
				GetPreview(preview.Uid).UpdateFromBackground(preview);

			mapScanComplete = true;
			backgroundMapScanInProgress = false;
			Game.BeforeGameStart -= CancelBackgroundMapScan;
			CleanupBackgroundMapScan(cancellation);

			// Apply filesystem changes that were queued while the background snapshot was parsed.
			UpdateMaps();
			if (RememberLobbyMap(Game.Settings.Server.Map))
				Game.Settings.Save();

			Log.Write("debug", $"Background map scan published {loaded.Count} previews.");
		}

		void FailBackgroundMapScan(ModData modData, int generation, CancellationTokenSource cancellation)
		{
			if (generation != backgroundMapScanGeneration)
			{
				CleanupBackgroundMapScan(cancellation);
				return;
			}

			backgroundMapScanInProgress = false;
			Game.BeforeGameStart -= CancelBackgroundMapScan;
			CleanupBackgroundMapScan(cancellation);
			Log.Write("debug", "Retrying the failed background map scan synchronously.");
			CompleteMapScan(modData);
		}

		void CleanupBackgroundMapScan(CancellationTokenSource cancellation)
		{
			if (cancellation == null)
				return;

			if (ReferenceEquals(backgroundMapScanCancellation, cancellation))
			{
				backgroundMapScanCancellation = null;
				backgroundMapScanTask = null;
			}

			cancellation.Dispose();
		}

		public void CancelBackgroundMapScan()
		{
			if (!backgroundMapScanInProgress)
				return;

			backgroundMapScanGeneration++;
			backgroundMapScanInProgress = false;
			Game.BeforeGameStart -= CancelBackgroundMapScan;
			backgroundMapScanCancellation?.Cancel();
		}

		public void StopBackgroundMapScan()
		{
			CancelBackgroundMapScan();
			try
			{
				backgroundMapScanTask?.Wait();
			}
			catch (AggregateException e) when (e.InnerExceptions.All(ex => ex is OperationCanceledException)) { }

			if (backgroundMapScanCancellation != null)
				CleanupBackgroundMapScan(backgroundMapScanCancellation);
		}

		public void CompleteMapScan(ModData modData)
		{
			if (mapScanComplete)
				return;

			CancelBackgroundMapScan();
			try
			{
				backgroundMapScanTask?.Wait();
			}
			catch (AggregateException e) when (e.InnerExceptions.All(ex => ex is OperationCanceledException)) { }

			var metrics = new MapCacheLoadMetrics();
			metrics.SetMapLocationCount(MapLocations.Count);
			var shellmaps = manifest.Shellmaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
			using (metrics.Measure(MapCacheLoadPhase.PreviewScan))
				foreach (var kv in MapLocations)
					foreach (var map in kv.Key.Contents)
						if (!kv.Value.HasFlag(MapClassification.System) || !shellmaps.Contains(map))
							LoadMapInternal(map, kv.Key, kv.Value, null, mapGridType, modDataRules, metrics);

			CleanupBackgroundMapScan(backgroundMapScanCancellation);
			mapScanComplete = true;
			metrics.Write("CompleteMapScan");
			LastModifiedMap = null;
			UpdateMaps();
		}

		public void LoadMap(string map, IReadOnlyPackage package, MapClassification classification, string oldMap)
		{
			LoadMapInternal(map, package, classification, oldMap);
		}

		MapPreview LoadMapInternal(string map, IReadOnlyPackage package, MapClassification classification, string oldMap,
			MapGridType? gridType = null, MiniYamlNode[][] modDataRules = null, MapCacheLoadMetrics metrics = null)
		{
			IReadOnlyPackage mapPackage = null;
			MapPreview preview = null;
			metrics?.AddMap();
			try
			{
				using (new PerfTimer(map))
				{
					using (metrics?.Measure(MapCacheLoadPhase.PackageOpen))
						mapPackage = package.OpenPackage(map, modFiles);

					if (mapPackage != null)
					{
						string uid;
						using (metrics?.Measure(MapCacheLoadPhase.UidHash))
							uid = Map.ComputeUID(mapPackage);

						preview = GetPreview(uid);
						using (metrics?.Measure(MapCacheLoadPhase.PreviewUpdate))
							preview.UpdateFromMapWithoutOwningPackage(
								mapPackage, package, classification, gridType, modDataRules, metrics);

						mapPackage.Dispose();

						if (oldMap != uid)
						{
							LastModifiedMap = uid;
							if (oldMap != null)
								mapUpdates[oldMap] = uid;
						}

						metrics?.AddLoadedMap();
						return preview;
					}
					else
						metrics?.AddNullPackage();
				}
			}
			catch (Exception e)
			{
				metrics?.AddFailedMap();
				mapPackage?.Dispose();
				Console.WriteLine($"Failed to load map: {map}");
				Console.WriteLine("Details:");
				Console.WriteLine(e);
				Log.Write("debug", $"Failed to load map: {map}");
				Log.Write("debug", "Details:");
				Log.Write("debug", e);
			}

			return preview;
		}

		public IEnumerable<IReadWritePackage> EnumerateMapDirPackages(MapClassification classification = MapClassification.System)
		{
			// Enumerate map directories
			foreach (var kv in manifest.MapFolders)
			{
				if (!Enum.TryParse(kv.Value, out MapClassification packageClassification))
					continue;

				if (!classification.HasFlag(packageClassification))
					continue;

				var name = kv.Key;
				var optional = name.StartsWith('~');
				if (optional)
					name = name[1..];

				// Don't try to open the map directory in the support directory if it doesn't exist
				var resolved = Platform.ResolvePath(name);
				if (resolved.StartsWith(Platform.SupportDir, StringComparison.Ordinal) && (!Directory.Exists(resolved) || !File.Exists(resolved)))
					continue;

				using (var package = (IReadWritePackage)modFiles.OpenPackage(name))
					yield return package;
			}
		}

		public IEnumerable<(IReadWritePackage Package, string Map)> EnumerateMapDirPackagesAndNames(MapClassification classification = MapClassification.System)
		{
			var mapDirPackages = EnumerateMapDirPackages(classification);

			foreach (var mapDirPackage in mapDirPackages)
				foreach (var map in mapDirPackage.Contents)
					yield return (mapDirPackage, map);
		}

		public IEnumerable<IReadWritePackage> EnumerateMapPackagesWithoutCaching(MapClassification classification = MapClassification.System)
		{
			var mapDirPackages = EnumerateMapDirPackages(classification);

			foreach (var mapDirPackage in mapDirPackages)
				foreach (var map in mapDirPackage.Contents)
					if (mapDirPackage.OpenPackage(map, modFiles) is IReadWritePackage mapPackage)
						yield return mapPackage;
		}

		public void GenerateMap(MapGenerationArgs args)
		{
			var p = GetPreview(args.Uid);

			// Skip regeneration if this map already exists. The UID is a content hash, so an
			// Available map with this UID is byte-for-byte what these args would produce -
			// whether it was generated earlier this session (Class Generated) or loaded from
			// disk (Class User). This mirrors the server's guard in the GenerateMap order
			// handler and avoids re-running the (slow) generator on every lobby round-trip.
			if (p.Class == MapClassification.Generated || p.Status == MapStatus.Available)
				return;

			p.UpdateFromGenerationArgs(args);

			Task.Run(() =>
			{
				try
				{
					var generator = Game.ModData.DefaultRules.Actors[SystemActors.EditorWorld]
						.TraitInfos<IMapGeneratorInfo>()
						.FirstOrDefault(info => info.Type == args.Generator);

					if (generator == null)
						throw new Exception($"Unknown map generator type {args.Generator}");

					var map = generator.Generate(Game.ModData, args);

					// Uid is generated when the map is saved
					map.Save(new ZipFileLoader.ReadWriteZipFile());

					if (map.Uid != args.Uid)
						throw new InvalidOperationException("Map generation UID mismatch");

					Game.RunAfterTick(() => p.UpdateFromMap(map.Package, MapClassification.Generated));
				}
				catch (Exception e)
				{
					Log.Write("debug", "Map generation failed with error:");
					Log.Write("debug", e);

					p.UpdateFromGenerationArgs(null);
				}
			});
		}


		public void QueryRemoteMapDetails(string repositoryUrl, IEnumerable<string> uids,
			Action<MapPreview> mapDetailsReceived = null, Action<MapPreview> mapQueryFailed = null)
		{
			var queryUids = uids.Distinct()
				.Where(uid => uid != null)
				.Select(GetPreview)
				.Where(p => p.Status == MapStatus.Unavailable)
				.Select(p => p.Uid)
				.ToList();

			foreach (var uid in queryUids)
				GetPreview(uid).BeginRemoteSearch();

			Task.Run(async () =>
			{
				var client = HttpClientFactory.Create();
				var stringPool = new HashSet<string>(); // Reuse common strings in YAML

				// Limit each query to 50 maps at a time to avoid request size limits
				foreach (var batchUids in queryUids.Chunk(50))
				{
					var url = repositoryUrl + "hash/" + string.Join(",", batchUids) + "/yaml";
					using (new PerfTimer("RemoteMapDetails"))
					{
						try
						{
							var result = await client.GetStreamAsync(url);
							foreach (var kv in MiniYaml.FromStream(result, url, stringPool: stringPool))
								GetPreview(kv.Key).CompleteRemoteSearch(kv.Value, mapDetailsReceived);
						}
						catch (Exception e)
						{
							Log.Write("debug", "Remote map query failed with error:");
							Log.Write("debug", e);
							Log.Write("debug", $"URL was: {url}");
						}

						foreach (var uid in batchUids)
						{
							var p = GetPreview(uid);
							if (p.Status == MapStatus.Searching)
								p.CompleteRemoteSearch(null, mapQueryFailed);
						}
					}
				}
			});
		}

		void LoadAsyncInternal()
		{
			Log.Write("debug", "MapCache.LoadAsyncInternal started");

			// Milliseconds to wait on one loop when nothing to do
			const int EmptyDelay = 50;

			// Keep the thread alive for at least 5 seconds after the last minimap generation
			const int MaxKeepAlive = 5000 / EmptyDelay;
			var keepAlive = MaxKeepAlive;

			while (true)
			{
				List<MapPreview> todo;
				lock (syncRoot)
				{
					todo = generateMinimap.Where(p => p.GetMinimap() == null).ToList();
					generateMinimap.Clear();
					if (keepAlive > 0)
						keepAlive--;
					if (keepAlive == 0 && todo.Count == 0)
					{
						previewLoaderThreadShutDown = true;
						break;
					}
				}

				if (todo.Count == 0)
				{
					Thread.Sleep(EmptyDelay);
					continue;
				}
				else
					keepAlive = MaxKeepAlive;

				// Render the minimap into the shared sheet
				foreach (var p in todo)
				{
					if (p.Preview != null)
					{
						Game.RunAfterTick(() =>
						{
							try
							{
								p.SetMinimap(sheetBuilder.Add(p.Preview));
							}
							catch (Exception e)
							{
								Log.Write("debug", "Failed to load minimap with exception:");
								Log.Write("debug", e);
							}
						});
					}

					// Yuck... But this helps the UI Jank when opening the map selector significantly.
					Thread.Sleep(Environment.ProcessorCount == 1 ? 25 : 5);
				}
			}

			// Release the buffer by forcing changes to be written out to the texture, allowing the buffer to be reclaimed by GC.
			if (sheetBuilder.Current != null)
				Game.RunAfterTick(sheetBuilder.Current.ReleaseBuffer);

			Log.Write("debug", "MapCache.LoadAsyncInternal ended");
		}

		public string GetUpdatedMap(string uid)
		{
			if (uid == null)
				return null;

			while (this[uid].Status != MapStatus.Available)
			{
				if (mapUpdates.TryGetValue(uid, out var newUid))
					uid = newUid;
				else
					return null;
			}

			return uid;
		}

		public void CacheMinimap(MapPreview preview)
		{
			bool launchPreviewLoaderThread;
			lock (syncRoot)
			{
				generateMinimap.Enqueue(preview);
				launchPreviewLoaderThread = previewLoaderThreadShutDown;
				previewLoaderThreadShutDown = false;
			}

			if (launchPreviewLoaderThread)
				Game.RunAfterTick(() =>
				{
					// Wait for any existing thread to exit before starting a new one.
					previewLoaderThread?.Join();

					previewLoaderThread = new Thread(LoadAsyncInternal)
					{
						Name = "Map Preview Loader",
						IsBackground = true
					};
					previewLoaderThread.Start();
				});
		}

		static bool IsAvailableLobbyMap(MapPreview map)
		{
			return map.Status == MapStatus.Available &&
				map.Visibility.HasFlag(MapVisibility.Lobby) &&
				(map.Class == MapClassification.System || map.Class == MapClassification.User);
		}

		bool IsSuitableInitialMap(MapPreview map)
		{
			if (!IsAvailableLobbyMap(map))
				return false;

			// Other map types may have confusing settings or gameplay
			if (!map.Categories.Contains("Conquest"))
				return false;

			// Maps with bots disabled confuse new players
			if (map.Players.Players.Any(x => !x.Value.AllowBots))
				return false;

			// Large maps expose unfortunate performance problems
			if (map.Bounds.Width > 128 || map.Bounds.Height > 128)
				return false;

			return true;
		}

		public bool RememberLobbyMap(string uid)
		{
			if (mapCacheSettings == null || string.IsNullOrEmpty(uid) || !TryGetPreview(uid, out var preview) ||
				!IsAvailableLobbyMap(preview) || string.IsNullOrEmpty(preview.Path))
				return false;

			if (mapCacheSettings.LobbyMap == uid && mapCacheSettings.LobbyMapPath == preview.Path)
				return false;

			mapCacheSettings.LobbyMap = uid;
			mapCacheSettings.LobbyMapPath = preview.Path;
			return true;
		}

		public string ChooseInitialMap(string initialUid, MersenneTwister random)
		{
			UpdateMaps();
			var map = string.IsNullOrEmpty(initialUid) ? null : GetPreview(initialUid);
			if (map == null ||
				map.Status != MapStatus.Available ||
				!map.Visibility.HasFlag(MapVisibility.Lobby) ||
				(map.Class != MapClassification.System && map.Class != MapClassification.User))
			{
				var previewSnapshot = GetPreviewsSnapshot();
				var selected = previewSnapshot
					.Where(m => m.Class == MapClassification.System && IsSuitableInitialMap(m))
					.RandomOrDefault(random) ??
					previewSnapshot.Where(IsSuitableInitialMap).RandomOrDefault(random) ??
					previewSnapshot.FirstOrDefault(m =>
						m.Status == MapStatus.Available &&
						m.Visibility.HasFlag(MapVisibility.Lobby) &&
						m.Class == MapClassification.System) ??
					previewSnapshot.FirstOrDefault(m =>
						m.Status == MapStatus.Available &&
						m.Visibility.HasFlag(MapVisibility.Lobby) &&
						m.Class == MapClassification.User);
				return selected == null ? string.Empty : selected.Uid;
			}

			return initialUid;
		}

		MapPreview GetPreview(string uid)
		{
			lock (previewsSync)
				return previews[uid];
		}

		bool TryGetPreview(string uid, out MapPreview preview)
		{
			lock (previewsSync)
				return previews.TryGetValue(uid, out preview);
		}

		MapPreview[] GetPreviewsSnapshot()
		{
			lock (previewsSync)
				return previews.Values.ToArray();
		}

		public MapPreview this[string key]
		{
			get
			{
				UpdateMaps();
				return GetPreview(key);
			}
		}

		public IEnumerator<MapPreview> GetEnumerator()
		{
			UpdateMaps();
			return ((IEnumerable<MapPreview>)GetPreviewsSnapshot()).GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		public void Dispose()
		{
			StopBackgroundMapScan();

			if (previewLoaderThread == null)
			{
				sheetBuilder.Dispose();
				return;
			}

			foreach (var p in GetPreviewsSnapshot())
				p.Dispose();

			foreach (var t in mapDirectoryTrackers)
				t.Dispose();

			// We need to let the loader thread exit before we can dispose our sheet builder.
			// Ideally we should dispose our resources before returning, but we don't to block waiting on the loader thread to exit.
			// Instead, we'll queue disposal to be run once it has exited.
			ThreadPool.QueueUserWorkItem(_ =>
			{
				previewLoaderThread.Join();
				Game.RunAfterTick(sheetBuilder.Dispose);
			});
		}
	}
}
