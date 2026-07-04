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
using OpenRA.FileSystem;
using OpenRA.Primitives;

namespace OpenRA.Graphics
{
	public interface ISpriteSequence
	{
		string Name { get; }
		int Length { get; }
		int Facings { get; }
		int Tick { get; }
		int ZOffset { get; }
		int ShadowZOffset { get; }
		Rectangle Bounds { get; }
		bool IgnoreWorldTint { get; }
		float Scale { get; }
		void Reserve(ModData modData, string tileset, SpriteCache cache);
		void ResolveSprites(SpriteCache cache);
		Sprite GetSprite(int frame);
		Sprite GetSprite(int frame, WAngle facing);
		(Sprite Sprite, WAngle Rotation) GetSpriteWithRotation(int frame, WAngle facing);
		Sprite GetShadow(int frame, WAngle facing);
		float GetAlpha(int frame);
	}

	public interface ISpriteSequenceLoader
	{
		IReadOnlyDictionary<string, ISpriteSequence> ParseSequences(ModData modData, string tileSet, SpriteCache cache, MiniYamlNode node);
	}

	public sealed class SequenceSet : IDisposable
	{
		public readonly string TileSet;
		readonly ModData modData;
		readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, ISpriteSequence>> images;
		public SpriteCache SpriteCache { get; }

		public SequenceSet(IReadOnlyFileSystem fileSystem, ModData modData, string tileSet, MiniYaml additionalSequences)
		{
			this.modData = modData;
			TileSet = tileSet;
			var rc = modData.Manifest.RendererConstants;
			SpriteCache = new SpriteCache(
				fileSystem, modData.SpriteLoaders,
				rc.SequenceBgraSheetSize, rc.SequenceIndexedSheetSize,
				modData.SpriteCachePool);
			using (new Support.PerfTimer("LoadSequences"))
				images = Load(fileSystem, additionalSequences);
		}

		public ISpriteSequence GetSequence(string image, string sequence)
		{
			if (!images.TryGetValue(image, out var sequences))
				throw new InvalidOperationException($"Image `{image}` does not have any sequences defined.");

			if (!sequences.TryGetValue(sequence, out var seq))
				throw new InvalidOperationException($"Image `{image}` does not have a sequence named `{sequence}`.");

			return seq;
		}

		public IEnumerable<string> Images => images.Keys;

		public bool HasSequence(string image, string sequence)
		{
			if (!images.TryGetValue(image, out var sequences))
				throw new InvalidOperationException($"Image `{image}` does not have any sequences defined.");

			return sequences.ContainsKey(sequence);
		}

		public IEnumerable<string> Sequences(string image)
		{
			if (!images.TryGetValue(image, out var sequences))
				throw new InvalidOperationException($"Image `{image}` does not have any sequences defined.");

			return sequences.Keys;
		}

		IReadOnlyDictionary<string, IReadOnlyDictionary<string, ISpriteSequence>> Load(IReadOnlyFileSystem fileSystem, MiniYaml additionalSequences)
		{
			var nodes = MiniYaml.Load(fileSystem, modData.Manifest.Sequences, additionalSequences);
			var images = new Dictionary<string, IReadOnlyDictionary<string, ISpriteSequence>>();
			foreach (var node in nodes)
			{
				// Nodes starting with ^ are inheritable but never loaded directly
				if (node.Key.StartsWith(ActorInfo.AbstractActorPrefix))
					continue;

				images[node.Key] = modData.SpriteSequenceLoader.ParseSequences(modData, TileSet, SpriteCache, node);
			}

			return images;
		}

		readonly HashSet<string> loadedImages = [];

		public void LoadSprites()
		{
			LoadImages(images.Keys);
		}

		// Incrementally load sprites for a subset of images. Images already loaded are skipped, so this can be
		// called repeatedly to append art after the initial pass: each call reserves only the not-yet-loaded
		// images and runs a fresh LoadReservations, which packs into new sheets (SpriteCache.BeginNewSession)
		// without touching or reloading previously-resident sheets. Unknown image names are ignored so callers
		// gating by faction/theme need not pre-filter to the exact available set.
		public void LoadImages(IEnumerable<string> imageNames)
		{
			var toResolve = new List<ISpriteSequence>();
			foreach (var image in imageNames)
			{
				if (loadedImages.Contains(image))
					continue;

				if (!images.TryGetValue(image, out var sequences))
					continue;

				loadedImages.Add(image);
				foreach (var sequence in sequences.Values)
				{
					sequence.Reserve(modData, TileSet, SpriteCache);
					toResolve.Add(sequence);
				}
			}

			if (toResolve.Count == 0)
				return;

			SpriteCache.LoadReservations(modData);
			foreach (var sequence in toResolve)
				sequence.ResolveSprites(SpriteCache);
		}

		// Reserve every image's sprites into the cache without resolving the sequences. Lets validation tooling
		// populate SpriteCache.MissingFiles for all referenced files after a LoadReservations pass. (Resolving
		// would throw on the first missing file; reservation records them all instead.)
		public void ReserveAllImages()
		{
			foreach (var sequences in images.Values)
				foreach (var sequence in sequences.Values)
					sequence.Reserve(modData, TileSet, SpriteCache);
		}

		public void Dispose()
		{
			SpriteCache.Dispose();
		}
	}
}
