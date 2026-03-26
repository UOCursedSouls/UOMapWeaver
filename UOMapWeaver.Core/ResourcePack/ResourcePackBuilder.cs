using System.Text.Json;
using System.Text.Json.Serialization;

using UOMapWeaver.Core.ResourcePack;

namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Builds a Minecraft-style resource pack from UO client files.
/// Extracts all sprites, metadata, and directional mappings into a self-contained
/// directory structure. After building, no UO client files are needed.
/// </summary>
public class ResourcePackBuilder
{
    private readonly SpriteExtractor _sprites;
    private readonly TileDataReader _tileData;
    private readonly string _radarColPath;
    private readonly string? _componentsDir;
    private readonly string? _musicDir;
    private readonly string? _soundUopPath;
    private readonly string? _animDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ResourcePackBuilder(string artIdxPath, string artMulPath,
        string tileDataPath, string radarColPath, string? componentsDir = null,
        string? musicDir = null, string? soundUopPath = null, string? animDir = null)
    {
        _sprites = new SpriteExtractor(artIdxPath, artMulPath);
        _tileData = new TileDataReader(tileDataPath);
        _radarColPath = radarColPath;
        _componentsDir = componentsDir;
        _musicDir = musicDir;
        _soundUopPath = soundUopPath;
        _animDir = animDir;
    }

    /// <summary>
    /// Build a COMPLETE resource pack extracting ALL sprites from art.mul,
    /// not just the ones used in Felucca. This is needed for the client
    /// which may request any sprite (UI gumps, character equipment, etc.).
    /// </summary>
    public ResourcePackStats BuildComplete(string outputDir,
        Action<string, int, int>? progress = null)
    {
        // Extract ALL land tiles (0-16383) and ALL static items (0-0x10000)
        var allLand = new HashSet<ushort>();
        for (ushort i = 0; i < 16384; i++)
            allLand.Add(i);

        var allStatics = new HashSet<ushort>();
        for (int i = 0; i < _tileData.ItemTiles.Length && i < 0x10000; i++)
            allStatics.Add((ushort)i);

        return Build(outputDir, allLand, allStatics, progress);
    }

    /// <summary>
    /// Build the resource pack with only specified tile IDs.
    /// </summary>
    /// <param name="outputDir">Output directory for the resource pack.</param>
    /// <param name="usedLandTileIds">Land tile IDs to extract.</param>
    /// <param name="usedStaticTileIds">Static tile IDs to extract.</param>
    /// <param name="progress">Progress callback (phase, current, total).</param>
    public ResourcePackStats Build(string outputDir,
        ISet<ushort> usedLandTileIds, ISet<ushort> usedStaticTileIds,
        Action<string, int, int>? progress = null)
    {
        var stats = new ResourcePackStats();
        Directory.CreateDirectory(outputDir);

        // Collect sprite dimensions for art_index.json (Minecraft-style metadata)
        var artIndex = new List<object>();
        var gumpIndex = new List<object>();

        // 1. Land tile sprites
        progress?.Invoke("Land sprites", 0, usedLandTileIds.Count);
        var landDir = Path.Combine(outputDir, "land");
        Directory.CreateDirectory(landDir);

        var landCount = 0;
        foreach (var tileId in usedLandTileIds.OrderBy(t => t))
        {
            var pngPath = Path.Combine(landDir, $"{tileId:D5}.png");
            var sprite = _sprites.ExtractLandSprite(tileId);
            if (sprite != null)
            {
                var (rgba, w, h) = sprite.Value;
                PngWriter.WriteFromRgba(pngPath, w, h, rgba);
                stats.LandSpritesSaved++;
                artIndex.Add(new { id = (int)tileId, width = w, height = h, length = rgba.Length / 4 });
            }
            else
            {
                stats.LandSpritesFailed++;
            }

            landCount++;
            if (landCount % 50 == 0)
                progress?.Invoke("Land sprites", landCount, usedLandTileIds.Count);
        }

        // 2. Static item sprites — organized by tiledata category
        progress?.Invoke("Static sprites", 0, usedStaticTileIds.Count);
        var staticsDir = Path.Combine(outputDir, "statics");

        var staticCount = 0;
        foreach (var tileId in usedStaticTileIds.OrderBy(t => t))
        {
            var category = CategorizeStatic(tileId);
            var categoryDir = Path.Combine(staticsDir, category);
            Directory.CreateDirectory(categoryDir);

            var pngPath = Path.Combine(categoryDir, $"{tileId:D5}.png");
            var sprite = _sprites.ExtractSprite(tileId);
            if (sprite != null)
            {
                var (rgba, w, h) = sprite.Value;
                PngWriter.WriteFromRgba(pngPath, w, h, rgba);
                stats.StaticSpritesSaved++;
                // Art index: static items start at 0x4000
                artIndex.Add(new { id = 0x4000 + (int)tileId, width = w, height = h, length = rgba.Length / 4 });
            }
            else
            {
                stats.StaticSpritesFailed++;
            }

            staticCount++;
            if (staticCount % 500 == 0)
                progress?.Invoke("Static sprites", staticCount, usedStaticTileIds.Count);
        }

        // 3. Tiledata — land tiles
        progress?.Invoke("Tiledata", 0, 2);
        var metadataDir = Path.Combine(outputDir, "metadata");
        Directory.CreateDirectory(metadataDir);

        var landTiledata = new List<object>();
        for (int i = 0; i < _tileData.LandTiles.Length; i++)
        {
            var td = _tileData.LandTiles[i];
            if (td == null) continue;
            landTiledata.Add(new
            {
                id = td.TileId,
                name = td.Name,
                flags = td.Flags.ToString(),
                textureId = td.TextureId
            });
        }
        File.WriteAllText(Path.Combine(metadataDir, "tiledata_land.json"),
            JsonSerializer.Serialize(landTiledata, JsonOpts));
        stats.MetadataFiles++;

        // 4. Tiledata — item tiles
        var itemTiledata = new List<object>();
        for (int i = 0; i < _tileData.ItemTiles.Length; i++)
        {
            var td = _tileData.ItemTiles[i];
            if (td == null) continue;
            if (string.IsNullOrWhiteSpace(td.Name) && td.Flags == TileFlag.None && td.Height == 0)
                continue; // Skip empty entries
            itemTiledata.Add(new
            {
                id = td.TileId,
                name = td.Name,
                flags = td.Flags.ToString(),
                height = td.Height,
                weight = td.Weight
            });
        }
        File.WriteAllText(Path.Combine(metadataDir, "tiledata_items.json"),
            JsonSerializer.Serialize(itemTiledata, JsonOpts));
        stats.MetadataFiles++;
        progress?.Invoke("Tiledata", 2, 2);

        // 5. Radar colors
        progress?.Invoke("Radar colors", 0, 1);
        if (File.Exists(_radarColPath))
        {
            var radarData = File.ReadAllBytes(_radarColPath);
            var landColors = new List<object>();
            var staticColors = new List<object>();

            // First 16384 entries = land colors
            for (int i = 0; i < Math.Min(16384, radarData.Length / 2); i++)
            {
                var color16 = (ushort)(radarData[i * 2] | (radarData[i * 2 + 1] << 8));
                var r = ((color16 >> 10) & 0x1F) * 255 / 31;
                var g = ((color16 >> 5) & 0x1F) * 255 / 31;
                var b = (color16 & 0x1F) * 255 / 31;
                landColors.Add(new { id = i, r, g, b });
            }

            // Remaining = static colors
            for (int i = 16384; i < radarData.Length / 2; i++)
            {
                var color16 = (ushort)(radarData[i * 2] | (radarData[i * 2 + 1] << 8));
                if (color16 == 0) continue; // Skip black/transparent
                var r = ((color16 >> 10) & 0x1F) * 255 / 31;
                var g = ((color16 >> 5) & 0x1F) * 255 / 31;
                var b = (color16 & 0x1F) * 255 / 31;
                staticColors.Add(new { id = i - 16384, r, g, b });
            }

            var radarJson = new { landColors, staticColors };
            File.WriteAllText(Path.Combine(metadataDir, "radarcol.json"),
                JsonSerializer.Serialize(radarJson, JsonOpts));
            stats.MetadataFiles++;
        }
        progress?.Invoke("Radar colors", 1, 1);

        // 6. Directional mappings from ModernUO Components
        progress?.Invoke("Directions", 0, 1);
        if (_componentsDir != null && Directory.Exists(_componentsDir))
        {
            var defs = ComponentsParser.ParseAll(_componentsDir);
            var directionsJson = defs.Select(d => new
            {
                d.Id,
                category = d.Category.ToString(),
                d.DisplayName,
                d.Height,
                d.Variants,
                d.Confidence,
                d.Tags
            });
            File.WriteAllText(Path.Combine(metadataDir, "directions.json"),
                JsonSerializer.Serialize(directionsJson, JsonOpts));
            stats.MetadataFiles++;
            stats.DirectionMappings = defs.Count;
        }
        progress?.Invoke("Directions", 1, 1);

        // 7. Gump sprites
        progress?.Invoke("Gumps", 0, 1);
        if (_animDir != null) // _animDir is the client directory
        {
            var gumpMulPath = Path.Combine(_animDir, "gumpart.mul");
            var gumpIdxPath = Path.Combine(_animDir, "gumpidx.mul");

            // Try case variations
            if (!File.Exists(gumpMulPath))
                gumpMulPath = Path.Combine(_animDir, "Gumpart.mul");
            if (!File.Exists(gumpIdxPath))
                gumpIdxPath = Path.Combine(_animDir, "Gumpidx.mul");

            if (File.Exists(gumpMulPath) && File.Exists(gumpIdxPath))
            {
                var gumpsDir = Path.Combine(outputDir, "gumps");
                stats.GumpsExtracted = _sprites.ExtractAllGumps(gumpMulPath, gumpIdxPath, gumpsDir);
                progress?.Invoke("Gumps", stats.GumpsExtracted, stats.GumpsExtracted);
            }
            else
            {
                // Convert UOP to MUL first, then extract
                var gumpUopPath = Path.Combine(_animDir, "gumpartLegacyMUL.uop");
                if (File.Exists(gumpUopPath))
                {
                    var tempMul = Path.Combine(outputDir, "_temp_gumpart.mul");
                    var tempIdx = Path.Combine(outputDir, "_temp_gumpidx.mul");

                    progress?.Invoke("Gumps (UOP→MUL)", 0, 1);
                    var converted = UopConverter.Convert(gumpUopPath, tempMul, tempIdx,
                        UopConverter.FileType.Gumpart);
                    progress?.Invoke("Gumps (UOP→MUL)", converted, converted);

                    if (converted > 0)
                    {
                        var gumpsDir = Path.Combine(outputDir, "gumps");
                        stats.GumpsExtracted = _sprites.ExtractAllGumps(tempMul, tempIdx, gumpsDir);
                        progress?.Invoke("Gumps (PNG)", stats.GumpsExtracted, stats.GumpsExtracted);
                    }

                    // Cleanup temp files
                    if (File.Exists(tempMul)) File.Delete(tempMul);
                    if (File.Exists(tempIdx)) File.Delete(tempIdx);
                }
            }
        }

        // 8. Music — copy MP3 files directly
        if (_musicDir != null && Directory.Exists(_musicDir))
        {
            var musicOutDir = Path.Combine(outputDir, "music");
            Directory.CreateDirectory(musicOutDir);
            var mp3Files = Directory.GetFiles(_musicDir, "*.mp3");
            progress?.Invoke("Music", 0, mp3Files.Length);

            for (int i = 0; i < mp3Files.Length; i++)
            {
                var destPath = Path.Combine(musicOutDir, Path.GetFileName(mp3Files[i]));
                File.Copy(mp3Files[i], destPath, overwrite: true);
                stats.MusicFilesCopied++;
                if (i % 20 == 0)
                    progress?.Invoke("Music", i, mp3Files.Length);
            }
            progress?.Invoke("Music", mp3Files.Length, mp3Files.Length);
        }

        // 8. Sounds — extract from soundLegacyMUL.uop
        if (_soundUopPath != null && File.Exists(_soundUopPath))
        {
            var soundOutDir = Path.Combine(outputDir, "sounds");
            Directory.CreateDirectory(soundOutDir);
            progress?.Invoke("Sounds", 0, 1);

            stats.SoundFilesExtracted = ExtractSounds(_soundUopPath, soundOutDir, progress);
            progress?.Invoke("Sounds done", stats.SoundFilesExtracted, stats.SoundFilesExtracted);
        }

        // 9. Animations — extract from anim.mul files
        if (_animDir != null && Directory.Exists(_animDir))
        {
            var animOutDir = Path.Combine(outputDir, "animations");
            Directory.CreateDirectory(animOutDir);
            progress?.Invoke("Animations", 0, 1);

            stats.AnimationFramesExtracted = ExtractAnimations(_animDir, animOutDir, progress);
            progress?.Invoke("Animations done", stats.AnimationFramesExtracted, stats.AnimationFramesExtracted);
        }

        // 10. Art index JSON (sprite dimensions for Entries[] replacement)
        progress?.Invoke("Art index", 0, 1);
        var metadataDir2 = Path.Combine(outputDir, "metadata");
        Directory.CreateDirectory(metadataDir2);

        File.WriteAllText(Path.Combine(metadataDir2, "art_index.json"),
            JsonSerializer.Serialize(artIndex, JsonOpts));
        stats.MetadataFiles++;

        // Gump index — collect during gump extraction
        if (stats.GumpsExtracted > 0)
        {
            // Re-scan gumps dir to build index from extracted PNGs
            var gumpsDir2 = Path.Combine(outputDir, "gumps");
            if (Directory.Exists(gumpsDir2))
            {
                foreach (var png in Directory.GetFiles(gumpsDir2, "*.png"))
                {
                    var name = Path.GetFileNameWithoutExtension(png);
                    if (!int.TryParse(name, out var gumpId)) continue;

                    // Read PNG dimensions from file header
                    var pngBytes = File.ReadAllBytes(png);
                    if (pngBytes.Length > 24 && pngBytes[0] == 0x89 && pngBytes[1] == 0x50)
                    {
                        // IHDR chunk: width at offset 16, height at offset 20 (big-endian)
                        var w = (pngBytes[16] << 24) | (pngBytes[17] << 16) | (pngBytes[18] << 8) | pngBytes[19];
                        var h = (pngBytes[20] << 24) | (pngBytes[21] << 16) | (pngBytes[22] << 8) | pngBytes[23];
                        gumpIndex.Add(new { id = gumpId, width = w, height = h, length = w * h });
                    }
                }
            }

            File.WriteAllText(Path.Combine(metadataDir2, "gump_index.json"),
                JsonSerializer.Serialize(gumpIndex, JsonOpts));
            stats.MetadataFiles++;
        }

        progress?.Invoke("Art index", 1, 1);

        // 11. Manifest
        var manifest = new
        {
            name = "MondainGem UO Resource Pack",
            version = "1.0.0",
            description = "Complete resource pack extracted from UO client files",
            source = "Ultima Online Classic Client + ModernUO Components",
            generated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            stats = new
            {
                landSprites = stats.LandSpritesSaved,
                staticSprites = stats.StaticSpritesSaved,
                metadataFiles = stats.MetadataFiles,
                directionMappings = stats.DirectionMappings,
                totalPngFiles = stats.LandSpritesSaved + stats.StaticSpritesSaved,
                gumps = stats.GumpsExtracted,
                musicFiles = stats.MusicFilesCopied,
                soundFiles = stats.SoundFilesExtracted,
                animationFrames = stats.AnimationFramesExtracted
            }
        };
        File.WriteAllText(Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return stats;
    }

    /// <summary>
    /// Extract sounds from soundLegacyMUL.uop. Each sound is 32-byte name + PCM data.
    /// Wraps in WAV header (22050 Hz, 16-bit, mono) and saves as .wav files.
    /// Format learned from UOFiddler/Ultima/Sound.cs.
    /// </summary>
    private static int ExtractSounds(string soundUopPath, string outputDir, Action<string, int, int>? progress)
    {
        // Try UOP format first, fall back to MUL
        var idxPath = Path.Combine(Path.GetDirectoryName(soundUopPath)!, "soundidx.mul");
        var mulPath = Path.Combine(Path.GetDirectoryName(soundUopPath)!, "sound.mul");

        // Try idx+mul first, then UOP
        byte[] idxData, mulData;

        if (File.Exists(idxPath) && File.Exists(mulPath))
        {
            idxData = File.ReadAllBytes(idxPath);
            mulData = File.ReadAllBytes(mulPath);
        }
        else if (soundUopPath.EndsWith(".uop", StringComparison.OrdinalIgnoreCase))
        {
            // Extract from UOP — the UOP wraps the MUL data
            // Pattern: "build/soundlegacymul/{0:D8}.dat"
            return ExtractSoundsFromUop(soundUopPath, outputDir, progress);
        }
        else
        {
            return 0;
        }

        var entryCount = idxData.Length / 12;
        var extracted = 0;

        for (int i = 0; i < entryCount && i < 0xFFF; i++)
        {
            var lookup = BitConverter.ToInt32(idxData, i * 12);
            var length = BitConverter.ToInt32(idxData, i * 12 + 4);

            if (lookup < 0 || length <= 32 || lookup + length > mulData.Length)
                continue;

            // First 32 bytes = null-terminated ASCII name
            var nameEnd = 32;
            for (int n = 0; n < 32; n++)
            {
                if (mulData[lookup + n] == 0) { nameEnd = n; break; }
            }
            var name = System.Text.Encoding.ASCII.GetString(mulData, lookup, nameEnd).Trim();
            if (string.IsNullOrEmpty(name)) name = $"sound_{i:D4}";

            // Sanitize filename
            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

            // PCM data starts after 32-byte name
            var pcmLength = length - 32;
            if (pcmLength <= 0) continue;

            // Build WAV file: RIFF header + PCM data
            // 22050 Hz, 16-bit, mono (from UOFiddler WaveHeader)
            var wavData = new byte[44 + pcmLength];

            // RIFF header
            wavData[0] = (byte)'R'; wavData[1] = (byte)'I'; wavData[2] = (byte)'F'; wavData[3] = (byte)'F';
            BitConverter.TryWriteBytes(wavData.AsSpan(4), 36 + pcmLength); // chunk size
            wavData[8] = (byte)'W'; wavData[9] = (byte)'A'; wavData[10] = (byte)'V'; wavData[11] = (byte)'E';

            // fmt chunk
            wavData[12] = (byte)'f'; wavData[13] = (byte)'m'; wavData[14] = (byte)'t'; wavData[15] = (byte)' ';
            BitConverter.TryWriteBytes(wavData.AsSpan(16), 16); // chunk size
            BitConverter.TryWriteBytes(wavData.AsSpan(20), (short)1); // PCM format
            BitConverter.TryWriteBytes(wavData.AsSpan(22), (short)1); // mono
            BitConverter.TryWriteBytes(wavData.AsSpan(24), 22050); // sample rate
            BitConverter.TryWriteBytes(wavData.AsSpan(28), 44100); // byte rate (22050 * 2)
            BitConverter.TryWriteBytes(wavData.AsSpan(32), (short)2); // block align
            BitConverter.TryWriteBytes(wavData.AsSpan(34), (short)16); // bits per sample

            // data chunk
            wavData[36] = (byte)'d'; wavData[37] = (byte)'a'; wavData[38] = (byte)'t'; wavData[39] = (byte)'a';
            BitConverter.TryWriteBytes(wavData.AsSpan(40), pcmLength);

            // Copy PCM data
            Array.Copy(mulData, lookup + 32, wavData, 44, pcmLength);

            var wavPath = Path.Combine(outputDir, $"{i:D4}_{safeName}.wav");
            File.WriteAllBytes(wavPath, wavData);
            extracted++;

            if (extracted % 100 == 0)
                progress?.Invoke("Sounds", extracted, entryCount);
        }

        return extracted;
    }

    /// <summary>
    /// Extract animations. Due to complexity (5 index files, System.Drawing dependency in UOFiddler),
    /// we create a metadata catalog of what animations exist rather than extracting all frames.
    /// Full frame extraction is Phase 4 (future work).
    /// </summary>
    private static int ExtractAnimations(string clientDir, string outputDir,
        Action<string, int, int>? progress)
    {
        var catalogEntries = new List<object>();
        int totalFrameEntries = 0;

        // Scan each anim index file to catalog what exists
        string[] animFiles = ["Anim.idx", "Anim2.idx", "Anim3.idx", "Anim4.idx", "Anim5.idx"];

        foreach (var animFile in animFiles)
        {
            var idxPath = Path.Combine(clientDir, animFile);
            if (!File.Exists(idxPath)) continue;

            var idxData = File.ReadAllBytes(idxPath);
            var entryCount = idxData.Length / 12;
            var validEntries = 0;

            for (int i = 0; i < entryCount; i++)
            {
                var lookup = BitConverter.ToInt32(idxData, i * 12);
                var length = BitConverter.ToInt32(idxData, i * 12 + 4);

                if (lookup >= 0 && length > 0)
                    validEntries++;
            }

            totalFrameEntries += validEntries;
            catalogEntries.Add(new
            {
                file = animFile,
                totalEntries = entryCount,
                validEntries,
                mulFile = animFile.Replace(".idx", ".mul")
            });

            progress?.Invoke($"Cataloging {animFile}", validEntries, entryCount);
        }

        // Also check UOP animation files
        string[] uopFiles = ["AnimationFrame1.uop", "AnimationFrame2.uop",
            "AnimationFrame3.uop", "AnimationFrame4.uop", "AnimationFrame6.uop"];

        foreach (var uopFile in uopFiles)
        {
            var uopPath = Path.Combine(clientDir, uopFile);
            if (!File.Exists(uopPath))
                continue;

            var fi = new FileInfo(uopPath);
            catalogEntries.Add(new
            {
                file = uopFile,
                sizeBytes = fi.Length,
                sizeMB = $"{fi.Length / 1024.0 / 1024.0:F1} MB",
                status = "NOT_EXTRACTED — requires frame-by-frame UOP parser"
            });
        }

        // Write catalog
        var catalog = new
        {
            status = "CATALOG_ONLY — full frame extraction is Phase 4",
            totalFrameEntries,
            files = catalogEntries
        };

        var json = JsonSerializer.Serialize(catalog, JsonOpts);
        File.WriteAllText(Path.Combine(outputDir, "animation_catalog.json"), json);

        return totalFrameEntries;
    }

    private static int ExtractSoundsFromUop(string uopPath, string outputDir,
        Action<string, int, int>? progress)
    {
        var uopData = File.ReadAllBytes(uopPath);
        if (uopData.Length < 28) return 0;

        // Check UOP magic "MYP\0"
        if (uopData[0] != 0x4D || uopData[1] != 0x59 || uopData[2] != 0x50)
            return 0;

        var pattern = "build/soundlegacymul/{0:D8}.dat";
        var entries = new Dictionary<int, (long offset, int length)>();

        // Parse UOP block chain
        var nextBlock = BitConverter.ToInt64(uopData, 12);

        while (nextBlock != 0 && nextBlock < uopData.Length)
        {
            var pos = (int)nextBlock;
            if (pos + 12 > uopData.Length) break;

            var filesCount = BitConverter.ToInt32(uopData, pos);
            nextBlock = BitConverter.ToInt64(uopData, pos + 4);
            pos += 12;

            for (int i = 0; i < filesCount; i++)
            {
                if (pos + 34 > uopData.Length) break;

                var fileOffset = BitConverter.ToInt64(uopData, pos);
                var headerLen = BitConverter.ToInt32(uopData, pos + 8);
                var compLen = BitConverter.ToInt32(uopData, pos + 12);
                var decompLen = BitConverter.ToInt32(uopData, pos + 16);
                var hash = BitConverter.ToUInt64(uopData, pos + 20);
                // skip dataHash (4 bytes) + flag (2 bytes)
                pos += 34;

                if (fileOffset == 0) continue;

                var dataOffset = fileOffset + headerLen;
                var dataLen = compLen > 0 ? compLen : decompLen;

                // Try to find which sound index this hash corresponds to
                for (int idx = 0; idx < 0xFFF; idx++)
                {
                    var fileName = string.Format(pattern, idx);
                    if (CreateUopHash(fileName) == hash)
                    {
                        entries[idx] = (dataOffset, dataLen);
                        break;
                    }
                }
            }
        }

        // Extract each sound
        var extracted = 0;
        foreach (var (soundId, (offset, length)) in entries.OrderBy(e => e.Key))
        {
            if (offset < 0 || offset + length > uopData.Length || length <= 32)
                continue;

            var dataPos = (int)offset;

            // First 32 bytes = name
            var nameEnd = 32;
            for (int n = 0; n < 32; n++)
            {
                if (uopData[dataPos + n] == 0) { nameEnd = n; break; }
            }
            var name = System.Text.Encoding.ASCII.GetString(uopData, dataPos, nameEnd).Trim();
            if (string.IsNullOrEmpty(name)) name = $"sound_{soundId:D4}";

            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

            var pcmLength = length - 32;
            if (pcmLength <= 0) continue;

            // Build WAV (22050 Hz, 16-bit, mono)
            var wavData = new byte[44 + pcmLength];
            wavData[0] = (byte)'R'; wavData[1] = (byte)'I'; wavData[2] = (byte)'F'; wavData[3] = (byte)'F';
            BitConverter.TryWriteBytes(wavData.AsSpan(4), 36 + pcmLength);
            wavData[8] = (byte)'W'; wavData[9] = (byte)'A'; wavData[10] = (byte)'V'; wavData[11] = (byte)'E';
            wavData[12] = (byte)'f'; wavData[13] = (byte)'m'; wavData[14] = (byte)'t'; wavData[15] = (byte)' ';
            BitConverter.TryWriteBytes(wavData.AsSpan(16), 16);
            BitConverter.TryWriteBytes(wavData.AsSpan(20), (short)1);
            BitConverter.TryWriteBytes(wavData.AsSpan(22), (short)1);
            BitConverter.TryWriteBytes(wavData.AsSpan(24), 22050);
            BitConverter.TryWriteBytes(wavData.AsSpan(28), 44100);
            BitConverter.TryWriteBytes(wavData.AsSpan(32), (short)2);
            BitConverter.TryWriteBytes(wavData.AsSpan(34), (short)16);
            wavData[36] = (byte)'d'; wavData[37] = (byte)'a'; wavData[38] = (byte)'t'; wavData[39] = (byte)'a';
            BitConverter.TryWriteBytes(wavData.AsSpan(40), pcmLength);

            Array.Copy(uopData, dataPos + 32, wavData, 44, pcmLength);

            File.WriteAllBytes(Path.Combine(outputDir, $"{soundId:D4}_{safeName}.wav"), wavData);
            extracted++;

            if (extracted % 100 == 0)
                progress?.Invoke("Sounds", extracted, entries.Count);
        }

        return extracted;
    }

    private static ulong CreateUopHash(string s)
    {
        uint eax, ecx, edx, ebx, esi, edi;
        eax = ecx = edx = ebx = esi = edi = 0;
        ebx = edi = esi = (uint)s.Length + 0xDEADBEEF;
        int i = 0;
        for (i = 0; i + 12 < s.Length; i += 12)
        {
            edi = (uint)((s[i + 7] << 24) | (s[i + 6] << 16) | (s[i + 5] << 8) | s[i + 4]) + edi;
            esi = (uint)((s[i + 11] << 24) | (s[i + 10] << 16) | (s[i + 9] << 8) | s[i + 8]) + esi;
            edx = (uint)((s[i + 3] << 24) | (s[i + 2] << 16) | (s[i + 1] << 8) | s[i]) - esi;
            edx = (edx + ebx) ^ (esi >> 28) ^ (esi << 4); esi += edi;
            edi = (edi - edx) ^ (edx >> 26) ^ (edx << 6); edx += esi;
            esi = (esi - edi) ^ (edi >> 24) ^ (edi << 8); edi += edx;
            ebx = (edx - esi) ^ (esi >> 16) ^ (esi << 16); esi += edi;
            edi = (edi - ebx) ^ (ebx >> 13) ^ (ebx << 19); ebx += esi;
            esi = (esi - edi) ^ (edi >> 28) ^ (edi << 4); edi += ebx;
        }
        if (s.Length - i > 0)
        {
            switch (s.Length - i)
            {
                case 12: esi += (uint)s[i + 11] << 24; goto case 11;
                case 11: esi += (uint)s[i + 10] << 16; goto case 10;
                case 10: esi += (uint)s[i + 9] << 8; goto case 9;
                case 9: esi += s[i + 8]; goto case 8;
                case 8: edi += (uint)s[i + 7] << 24; goto case 7;
                case 7: edi += (uint)s[i + 6] << 16; goto case 6;
                case 6: edi += (uint)s[i + 5] << 8; goto case 5;
                case 5: edi += s[i + 4]; goto case 4;
                case 4: ebx += (uint)s[i + 3] << 24; goto case 3;
                case 3: ebx += (uint)s[i + 2] << 16; goto case 2;
                case 2: ebx += (uint)s[i + 1] << 8; goto case 1;
                case 1: ebx += s[i]; break;
            }
            esi = (esi ^ edi) - ((edi >> 18) ^ (edi << 14));
            ecx = (esi ^ ebx) - ((esi >> 21) ^ (esi << 11));
            edi = (edi ^ ecx) - ((ecx >> 7) ^ (ecx << 25));
            esi = (esi ^ edi) - ((edi >> 16) ^ (edi << 16));
            edx = (esi ^ ecx) - ((esi >> 28) ^ (esi << 4));
            edi = (edi ^ edx) - ((edx >> 18) ^ (edx << 14));
            eax = (esi ^ edi) - ((edi >> 8) ^ (edi << 24));
            return ((ulong)edi << 32) | eax;
        }
        return ((ulong)esi << 32) | eax;
    }

    private string CategorizeStatic(ushort tileId)
    {
        // Categorize by tiledata flags
        if (tileId >= _tileData.ItemTiles.Length || _tileData.ItemTiles[tileId] == null)
            return "unknown";

        var flags = _tileData.ItemTiles[tileId].Flags;
        if ((flags & TileFlag.Wall) != 0) return "wall";
        if ((flags & TileFlag.Door) != 0) return "door";
        if ((flags & TileFlag.Roof) != 0) return "roof";
        if ((flags & TileFlag.Foliage) != 0) return "vegetation";
        if ((flags & TileFlag.Wet) != 0) return "water";
        if ((flags & TileFlag.LightSource) != 0) return "light";
        if ((flags & TileFlag.Container) != 0) return "container";
        if ((flags & TileFlag.Surface) != 0) return "surface";
        if ((flags & TileFlag.Impassable) != 0) return "obstacle";
        return "decoration";
    }
}

public class ResourcePackStats
{
    public int LandSpritesSaved { get; set; }
    public int LandSpritesFailed { get; set; }
    public int StaticSpritesSaved { get; set; }
    public int StaticSpritesFailed { get; set; }
    public int MetadataFiles { get; set; }
    public int DirectionMappings { get; set; }
    public int GumpsExtracted { get; set; }
    public int MusicFilesCopied { get; set; }
    public int SoundFilesExtracted { get; set; }
    public int AnimationFramesExtracted { get; set; }
}
