using System.Text.Json;

namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Extracts animation frames from UO anim*.mul/idx files and saves them as individual PNGs.
/// Each body gets a directory, each action/direction/frame gets a PNG file.
///
/// Output structure:
///   animations/
///     {bodyId:D5}/
///       {action:D2}_{direction}_{frame:D2}.png
///     animation_index.json
///
/// No MUL files are copied — everything is extracted to PNG + JSON metadata.
/// </summary>
public class AnimationExtractor
{
    // ARGB1555 → RGB8 lookup (same as SpriteExtractor)
    private static readonly byte[] ColorLUT =
    [
        0x00, 0x08, 0x10, 0x18, 0x20, 0x29, 0x31, 0x39,
        0x41, 0x4A, 0x52, 0x5A, 0x62, 0x6A, 0x73, 0x7B,
        0x83, 0x8B, 0x94, 0x9C, 0xA4, 0xAC, 0xB4, 0xBD,
        0xC5, 0xCD, 0xD5, 0xDE, 0xE6, 0xEE, 0xF6, 0xFF
    ];

    private const int MAX_DIRECTIONS = 5;
    private const int IDX_ENTRY_SIZE = 12; // AnimIdxBlock: position(4) + size(4) + unknown(4)

    // Actions per animation type
    private const int MONSTER_ACTIONS = 22;
    private const int ANIMAL_ACTIONS = 13;
    private const int HUMAN_ACTIONS = 35;

    /// <summary>
    /// Represents a decoded animation frame ready to be saved as PNG.
    /// </summary>
    public struct FrameInfo
    {
        public short CenterX;
        public short CenterY;
        public short Width;
        public short Height;
        public byte[] Rgba; // RGBA byte array for PngWriter
    }

    /// <summary>
    /// Extract all animation frames from a set of anim*.mul/idx pairs.
    /// Returns the total number of frames extracted.
    /// </summary>
    public static int ExtractAll(string clientDir, string outputDir,
        Action<string, int, int>? progress = null)
    {
        Directory.CreateDirectory(outputDir);

        int totalFrames = 0;
        var indexEntries = new List<object>();

        // Process each anim MUL/IDX pair
        string[] animPairs =
        [
            "anim", "anim2", "anim3", "anim4", "anim5"
        ];

        for (int fileIdx = 0; fileIdx < animPairs.Length; fileIdx++)
        {
            var baseName = animPairs[fileIdx];
            var idxPath = FindFile(clientDir, baseName + ".idx");
            var mulPath = FindFile(clientDir, baseName + ".mul");

            if (idxPath == null || mulPath == null)
                continue;

            progress?.Invoke($"Reading {baseName}.mul", fileIdx, animPairs.Length);

            var idxData = File.ReadAllBytes(idxPath);
            var mulData = File.ReadAllBytes(mulPath);

            var extracted = ExtractFromMul(fileIdx, idxData, mulData, outputDir, indexEntries, progress);
            totalFrames += extracted;

            progress?.Invoke($"{baseName}: {extracted} frames", fileIdx + 1, animPairs.Length);
        }

        // Write animation index JSON
        progress?.Invoke("Writing animation_index.json", 0, 1);
        var index = new
        {
            totalFrames,
            totalBodies = indexEntries.Count,
            bodies = indexEntries
        };

        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, "animation_index.json"), json);

        return totalFrames;
    }

    /// <summary>
    /// Extract all valid animation frames from a single anim MUL/IDX pair.
    /// </summary>
    private static int ExtractFromMul(int fileIndex, byte[] idxData, byte[] mulData,
        string outputDir, List<object> indexEntries,
        Action<string, int, int>? progress)
    {
        int totalEntries = idxData.Length / IDX_ENTRY_SIZE;
        int framesExtracted = 0;

        // Scan all idx entries to find valid animations
        // We need to reverse-map flat index → (body, action, direction)
        // The layout depends on file index and body type

        // For file 0 (anim.mul), the layout is:
        //   Monsters (body 0-199): offset = body * 110 entries (22 actions × 5 dirs)
        //   Animals (body 200-399): offset = 22000 + (body-200) * 65 entries (13 actions × 5 dirs)
        //   Humans (body 400+): offset = 35000 + (body-400) * 175 entries (35 actions × 5 dirs)
        //
        // For file 1+ the ranges differ, but the structure is similar.
        // We scan ALL entries and reverse-map them.

        var bodyAnimations = new Dictionary<int, BodyAnimData>();

        // Iterate through all idx entries
        for (int i = 0; i < totalEntries; i++)
        {
            int position = BitConverter.ToInt32(idxData, i * IDX_ENTRY_SIZE);
            int size = BitConverter.ToInt32(idxData, i * IDX_ENTRY_SIZE + 4);

            if (position < 0 || size <= 0 || position + size > mulData.Length)
                continue;

            // Reverse-map entry index to (body, action, direction)
            var mapping = ReverseMapEntry(i, totalEntries, fileIndex);
            if (mapping == null)
                continue;

            var (body, action, direction) = mapping.Value;

            // Decode frames from MUL data
            var frames = DecodeFrames(mulData, position, size);
            if (frames == null || frames.Count == 0)
                continue;

            // Create body directory
            var bodyDir = Path.Combine(outputDir, $"{body:D5}");
            Directory.CreateDirectory(bodyDir);

            // Track body animation data for index
            if (!bodyAnimations.TryGetValue(body, out var bodyData))
            {
                bodyData = new BodyAnimData { Body = body, FileIndex = fileIndex };
                bodyAnimations[body] = bodyData;
            }

            // Save each frame as PNG
            for (int f = 0; f < frames.Count; f++)
            {
                var frame = frames[f];
                if (frame.Width <= 0 || frame.Height <= 0 || frame.Rgba == null)
                    continue;

                var pngPath = Path.Combine(bodyDir, $"{action:D2}_{direction}_{f:D2}.png");
                PngWriter.WriteFromRgba(pngPath, frame.Width, frame.Height, frame.Rgba);
                framesExtracted++;

                bodyData.Actions.TryAdd(action, new ActionData());
                var actionData = bodyData.Actions[action];
                actionData.Directions.TryAdd(direction, new DirectionData());
                var dirData = actionData.Directions[direction];
                dirData.Frames.Add(new FrameMeta
                {
                    Index = f,
                    Width = frame.Width,
                    Height = frame.Height,
                    CenterX = frame.CenterX,
                    CenterY = frame.CenterY
                });
            }

            if (framesExtracted % 500 == 0)
                progress?.Invoke($"Extracting frames", framesExtracted, totalEntries);
        }

        // Add body entries to index
        foreach (var kvp in bodyAnimations.OrderBy(k => k.Key))
        {
            var bd = kvp.Value;
            var actions = new Dictionary<string, object>();
            foreach (var actionKvp in bd.Actions.OrderBy(k => k.Key))
            {
                var dirs = new Dictionary<string, object>();
                foreach (var dirKvp in actionKvp.Value.Directions.OrderBy(k => k.Key))
                {
                    dirs[$"{dirKvp.Key}"] = dirKvp.Value.Frames.Select(f => new
                    {
                        frame = f.Index,
                        w = f.Width,
                        h = f.Height,
                        cx = f.CenterX,
                        cy = f.CenterY
                    }).ToArray();
                }
                actions[$"{actionKvp.Key:D2}"] = dirs;
            }

            indexEntries.Add(new
            {
                body = bd.Body,
                fileIndex = bd.FileIndex,
                actions
            });
        }

        return framesExtracted;
    }

    /// <summary>
    /// Decode animation frames from raw MUL data at the given position.
    /// Format: 512-byte palette → frame count → frame offsets → RLE frame data.
    /// </summary>
    private static List<FrameInfo>? DecodeFrames(byte[] mulData, int position, int size)
    {
        if (position + 512 + 4 > mulData.Length)
            return null;

        // Read 256-color palette (512 bytes = 256 × 16-bit colors)
        var palette = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            palette[i] = BitConverter.ToUInt16(mulData, position + i * 2);
        }

        int dataStart = position + 512;
        if (dataStart + 4 > mulData.Length)
            return null;

        uint frameCount = BitConverter.ToUInt32(mulData, dataStart);
        if (frameCount == 0 || frameCount > 50) // sanity check
            return null;

        // Read frame offsets (relative to dataStart)
        if (dataStart + 4 + frameCount * 4 > mulData.Length)
            return null;

        var frameOffsets = new uint[frameCount];
        for (int i = 0; i < (int)frameCount; i++)
        {
            frameOffsets[i] = BitConverter.ToUInt32(mulData, dataStart + 4 + i * 4);
        }

        var frames = new List<FrameInfo>();

        for (int i = 0; i < (int)frameCount; i++)
        {
            int framePos = (int)(dataStart + frameOffsets[i]);
            if (framePos + 8 > mulData.Length)
            {
                frames.Add(default);
                continue;
            }

            var frame = DecodeFrame(mulData, framePos, palette);
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>
    /// Decode a single RLE-compressed animation frame.
    /// </summary>
    private static FrameInfo DecodeFrame(byte[] data, int pos, ushort[] palette)
    {
        var frame = new FrameInfo();

        if (pos + 8 > data.Length)
            return frame;

        frame.CenterX = BitConverter.ToInt16(data, pos);
        frame.CenterY = BitConverter.ToInt16(data, pos + 2);
        frame.Width = BitConverter.ToInt16(data, pos + 4);
        frame.Height = BitConverter.ToInt16(data, pos + 6);

        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > 1024 || frame.Height > 1024)
            return frame;

        frame.Rgba = new byte[frame.Width * frame.Height * 4]; // transparent by default

        int offset = pos + 8;

        // RLE decoding
        while (offset + 4 <= data.Length)
        {
            uint header = BitConverter.ToUInt32(data, offset);
            offset += 4;

            if (header == 0x7FFF7FFF) // end marker
                break;

            int runLength = (int)(header & 0x0FFF);

            // X offset (bits 22-31, signed 10-bit)
            int xOff = (int)((header >> 22) & 0x03FF);
            if ((xOff & 0x0200) != 0)
                xOff |= unchecked((int)0xFFFFFE00);

            // Y offset (bits 12-21, signed 10-bit)
            int yOff = (int)((header >> 12) & 0x3FF);
            if ((yOff & 0x0200) != 0)
                yOff |= unchecked((int)0xFFFFFE00);

            int x = xOff + frame.CenterX;
            int y = yOff + frame.CenterY + frame.Height;

            if (offset + runLength > data.Length)
                break;

            for (int k = 0; k < runLength; k++)
            {
                byte paletteIdx = data[offset++];
                ushort color16 = palette[paletteIdx];

                int px = y * frame.Width + x + k;
                if (px < 0 || px >= frame.Width * frame.Height)
                    continue;

                // Convert 16-bit color to RGBA (same as SpriteExtractor)
                int rgbaIdx = px * 4;
                frame.Rgba[rgbaIdx] = ColorLUT[(color16 >> 10) & 0x1F];     // R
                frame.Rgba[rgbaIdx + 1] = ColorLUT[(color16 >> 5) & 0x1F];  // G
                frame.Rgba[rgbaIdx + 2] = ColorLUT[color16 & 0x1F];         // B
                frame.Rgba[rgbaIdx + 3] = (color16 != 0) ? (byte)255 : (byte)0; // A
            }
        }

        return frame;
    }

    /// <summary>
    /// Reverse-map a flat idx entry index to (body, action, direction).
    /// Returns null if the entry doesn't map to a valid animation.
    /// </summary>
    private static (int body, int action, int direction)? ReverseMapEntry(
        int entryIndex, int totalEntries, int fileIndex)
    {
        // File 0 (anim.mul) layout:
        //   [0..21999]        → Monsters body 0-199, 110 entries each (22 actions × 5 dirs)
        //   [22000..34999]    → Animals body 200-399, 65 entries each (13 actions × 5 dirs)
        //   [35000..]         → Humans body 400+, 175 entries each (35 actions × 5 dirs)

        if (fileIndex == 0)
        {
            if (entryIndex < 22000)
            {
                // Monster range
                int body = entryIndex / (MONSTER_ACTIONS * MAX_DIRECTIONS);
                int rem = entryIndex % (MONSTER_ACTIONS * MAX_DIRECTIONS);
                int action = rem / MAX_DIRECTIONS;
                int direction = rem % MAX_DIRECTIONS;
                if (body < 200)
                    return (body, action, direction);
            }
            else if (entryIndex < 35000)
            {
                // Animal range
                int localIdx = entryIndex - 22000;
                int body = 200 + localIdx / (ANIMAL_ACTIONS * MAX_DIRECTIONS);
                int rem = localIdx % (ANIMAL_ACTIONS * MAX_DIRECTIONS);
                int action = rem / MAX_DIRECTIONS;
                int direction = rem % MAX_DIRECTIONS;
                if (body < 400)
                    return (body, action, direction);
            }
            else
            {
                // Human range
                int localIdx = entryIndex - 35000;
                int body = 400 + localIdx / (HUMAN_ACTIONS * MAX_DIRECTIONS);
                int rem = localIdx % (HUMAN_ACTIONS * MAX_DIRECTIONS);
                int action = rem / MAX_DIRECTIONS;
                int direction = rem % MAX_DIRECTIONS;
                return (body, action, direction);
            }
        }
        else
        {
            // Files 1+ (anim2.mul, anim3.mul, etc.) use the same layout structure
            // but body ID ranges are remapped by .def files at runtime.
            // For extraction, we use the same layout as file 0 — the body IDs
            // will be the raw indices; .def remapping happens at client load time.
            if (entryIndex < 22000)
            {
                int body = entryIndex / (MONSTER_ACTIONS * MAX_DIRECTIONS);
                int rem = entryIndex % (MONSTER_ACTIONS * MAX_DIRECTIONS);
                int action = rem / MAX_DIRECTIONS;
                int direction = rem % MAX_DIRECTIONS;
                if (body < 200)
                    return (body + fileIndex * 1000, action, direction); // offset body by file index
            }
            else if (entryIndex < 35000)
            {
                int localIdx = entryIndex - 22000;
                int body = 200 + localIdx / (ANIMAL_ACTIONS * MAX_DIRECTIONS);
                int rem = localIdx % (ANIMAL_ACTIONS * MAX_DIRECTIONS);
                int action = rem / MAX_DIRECTIONS;
                int direction = rem % MAX_DIRECTIONS;
                if (body < 400)
                    return (body + fileIndex * 1000, action, direction);
            }
            else
            {
                int localIdx = entryIndex - 35000;
                int body = 400 + localIdx / (HUMAN_ACTIONS * MAX_DIRECTIONS);
                int rem = localIdx % (HUMAN_ACTIONS * MAX_DIRECTIONS);
                int action = rem / MAX_DIRECTIONS;
                int direction = rem % MAX_DIRECTIONS;
                return (body + fileIndex * 1000, action, direction);
            }
        }

        return null;
    }

    /// <summary>
    /// Find a file case-insensitively in the given directory.
    /// </summary>
    private static string? FindFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) return path;

        // Try case-insensitive match
        if (!Directory.Exists(dir)) return null;
        foreach (var file in Directory.GetFiles(dir))
        {
            if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    // --- Helper types for building the index ---

    private class BodyAnimData
    {
        public int Body;
        public int FileIndex;
        public SortedDictionary<int, ActionData> Actions = new();
    }

    private class ActionData
    {
        public SortedDictionary<int, DirectionData> Directions = new();
    }

    private class DirectionData
    {
        public List<FrameMeta> Frames = new();
    }

    private class FrameMeta
    {
        public int Index;
        public int Width;
        public int Height;
        public short CenterX;
        public short CenterY;
    }
}
