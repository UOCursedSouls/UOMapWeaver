using UOMapWeaver.Core.ResourcePack;

namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Extracts individual static item sprites from art.mul/uop and saves them as PNG files.
/// Creates Minecraft-style resource pack structure where each item has its definition
/// JSON alongside PNG sprites for each directional variant.
///
/// Output structure:
///   ItemRegistry/
///     walls/
///       wall_granite_standard_walls/
///         definition.json
///         south_200.png
///         east_201.png
///         corner_199.png
///         post_204.png
///     doors/
///       door_dark_wood_door/
///         definition.json
///         southclosed_1701.png
///         eastclosed_1709.png
/// </summary>
public class SpriteExtractor
{
    private byte[] _artData;
    private int[] _artOffsets = [];
    private int[] _artLengths = [];
    private int _artEntryCount;

    // ARGB1555 → RGB8 lookup
    private static readonly byte[] ColorLUT =
    [
        0x00, 0x08, 0x10, 0x18, 0x20, 0x29, 0x31, 0x39,
        0x41, 0x4A, 0x52, 0x5A, 0x62, 0x6A, 0x73, 0x7B,
        0x83, 0x8B, 0x94, 0x9C, 0xA4, 0xAC, 0xB4, 0xBD,
        0xC5, 0xCD, 0xD5, 0xDE, 0xE6, 0xEE, 0xF6, 0xFF
    ];

    public SpriteExtractor(string artIdxPath, string artMulPath)
    {
        _artData = File.ReadAllBytes(artMulPath);

        if (artMulPath.EndsWith(".uop", StringComparison.OrdinalIgnoreCase))
        {
            LoadFromUop(artMulPath);
        }
        else
        {
            LoadFromMul(artIdxPath);
        }
    }

    /// <summary>
    /// Extract a single static item sprite and return as RGBA pixel buffer.
    /// Returns null if the sprite doesn't exist or is invalid.
    /// </summary>
    public (byte[] Rgba, int Width, int Height)? ExtractSprite(ushort itemId)
    {
        var artIdx = 0x4000 + itemId;
        if (artIdx >= _artEntryCount || _artOffsets[artIdx] < 0 || _artLengths[artIdx] <= 8)
            return null;

        var offset = _artOffsets[artIdx];
        if (offset + 8 > _artData.Length)
            return null;

        var width = (short)(_artData[offset + 4] | (_artData[offset + 5] << 8));
        var height = (short)(_artData[offset + 6] | (_artData[offset + 7] << 8));

        if (width <= 0 || height <= 0 || width > 512 || height > 512)
            return null;

        var rgba = new byte[width * height * 4]; // Transparent by default

        var tableStart = offset + 8;
        if (tableStart + height * 2 > _artData.Length)
            return null;

        var dataStart = tableStart + height * 2;

        for (int row = 0; row < height; row++)
        {
            var lineOffset = (ushort)(_artData[tableStart + row * 2] | (_artData[tableStart + row * 2 + 1] << 8));
            var rlePos = dataStart + lineOffset * 2;

            var x = 0;
            while (rlePos + 3 < _artData.Length)
            {
                var xOffset = (ushort)(_artData[rlePos] | (_artData[rlePos + 1] << 8));
                var run = (ushort)(_artData[rlePos + 2] | (_artData[rlePos + 3] << 8));
                rlePos += 4;

                if (xOffset + run >= 2048) break;
                if (xOffset + run == 0) break;

                x += xOffset;

                for (int j = 0; j < run && rlePos + 1 < _artData.Length; j++)
                {
                    var color16 = (ushort)(_artData[rlePos] | (_artData[rlePos + 1] << 8));
                    rlePos += 2;

                    if (color16 != 0 && x + j < width)
                    {
                        var px = (row * width + x + j) * 4;
                        rgba[px] = ColorLUT[(color16 >> 10) & 0x1F];     // R
                        rgba[px + 1] = ColorLUT[(color16 >> 5) & 0x1F];  // G
                        rgba[px + 2] = ColorLUT[color16 & 0x1F];         // B
                        rgba[px + 3] = 255;                               // A
                    }
                }

                x += run;
            }
        }

        return (rgba, width, height);
    }

    /// <summary>
    /// Extract a land tile (44x44 diamond) sprite.
    /// </summary>
    public (byte[] Rgba, int Width, int Height)? ExtractLandSprite(ushort tileId)
    {
        if (tileId >= 0x4000) return null;
        var artIdx = tileId;
        if (artIdx >= _artEntryCount || _artOffsets[artIdx] < 0 || _artLengths[artIdx] <= 0)
            return null;

        var offset = _artOffsets[artIdx];
        if (offset + 1888 > _artData.Length)
            return null;

        var rgba = new byte[44 * 44 * 4];
        var dataPos = offset;

        // Upper half
        for (int row = 0; row < 22; row++)
        {
            var count = (row + 1) * 2;
            var sx = 22 - (row + 1);
            for (int col = 0; col < count; col++)
            {
                if (dataPos + 1 < _artData.Length)
                {
                    var color16 = (ushort)(_artData[dataPos] | (_artData[dataPos + 1] << 8));
                    if (color16 != 0)
                    {
                        var px = (row * 44 + sx + col) * 4;
                        rgba[px] = ColorLUT[(color16 >> 10) & 0x1F];
                        rgba[px + 1] = ColorLUT[(color16 >> 5) & 0x1F];
                        rgba[px + 2] = ColorLUT[color16 & 0x1F];
                        rgba[px + 3] = 255;
                    }
                }
                dataPos += 2;
            }
        }

        // Lower half
        for (int row = 0; row < 22; row++)
        {
            var count = (22 - row) * 2;
            var sx = row;
            for (int col = 0; col < count; col++)
            {
                if (dataPos + 1 < _artData.Length)
                {
                    var color16 = (ushort)(_artData[dataPos] | (_artData[dataPos + 1] << 8));
                    if (color16 != 0)
                    {
                        var px = ((22 + row) * 44 + sx + col) * 4;
                        rgba[px] = ColorLUT[(color16 >> 10) & 0x1F];
                        rgba[px + 1] = ColorLUT[(color16 >> 5) & 0x1F];
                        rgba[px + 2] = ColorLUT[color16 & 0x1F];
                        rgba[px + 3] = 255;
                    }
                }
                dataPos += 2;
            }
        }

        return (rgba, 44, 44);
    }

    /// <summary>
    /// Save a sprite as PNG file.
    /// </summary>
    public bool SaveSpritePng(ushort itemId, string outputPath, bool isLand = false)
    {
        var sprite = isLand ? ExtractLandSprite(itemId) : ExtractSprite(itemId);
        if (sprite == null) return false;

        var (rgba, w, h) = sprite.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        PngWriter.WriteFromRgba(outputPath, w, h, rgba);
        return true;
    }

    // ExportResourcePack removed — use ResourcePackBuilder instead

    /// <summary>
    /// Extract a gump sprite from gumpart.mul/gumpartLegacyMUL.uop.
    /// Gump format: row lookup table + RLE (value16 + run16 per segment).
    /// </summary>
    public (byte[] Rgba, int Width, int Height)? ExtractGumpSprite(int gumpId,
        byte[] gumpData, int[] gumpOffsets, int[] gumpLengths, int gumpEntryCount,
        int[]? gumpWidths = null, int[]? gumpHeights = null)
    {
        if (gumpId < 0 || gumpId >= gumpEntryCount)
            return null;

        var offset = gumpOffsets[gumpId];
        var length = gumpLengths[gumpId];
        if (offset < 0 || length <= 0 || offset + length > gumpData.Length)
            return null;

        // Width/Height from index extra field
        var w = gumpWidths != null && gumpId < gumpWidths.Length ? gumpWidths[gumpId] : 0;
        var h = gumpHeights != null && gumpId < gumpHeights.Length ? gumpHeights[gumpId] : 0;
        if (w <= 0 || h <= 0 || w > 32768 || h > 32768)
            return null;

        // Safety check: don't allocate more than 256MB
        long pixelCount = (long)w * h;
        if (pixelCount > 64_000_000) // 64M pixels * 4 = 256MB
            return null;

        var rgba = new byte[w * h * 4]; // Transparent default
        var pos = offset;

        // Row lookup table: h entries, each is an int32 (offset into data in uint16 units)
        if (pos + h * 4 > gumpData.Length)
            return null;

        var rowOffsets = new int[h];
        for (int i = 0; i < h; i++)
        {
            rowOffsets[i] = BitConverter.ToInt32(gumpData, pos + i * 4);
        }

        var dataStart = pos;

        for (int y = 0; y < h; y++)
        {
            var rlePos = dataStart + rowOffsets[y] * 4; // * 4 because values are in uint16 pairs
            var x = 0;
            var nextRow = y < h - 1 ? rowOffsets[y + 1] : (length / 4);
            var gSize = nextRow - rowOffsets[y];

            for (int i = 0; i < gSize && rlePos + 3 < gumpData.Length; i++)
            {
                var color16 = (ushort)(gumpData[rlePos] | (gumpData[rlePos + 1] << 8));
                var run = (ushort)(gumpData[rlePos + 2] | (gumpData[rlePos + 3] << 8));
                rlePos += 4;

                if (color16 != 0)
                {
                    var r = ColorLUT[(color16 >> 10) & 0x1F];
                    var g = ColorLUT[(color16 >> 5) & 0x1F];
                    var b = ColorLUT[color16 & 0x1F];

                    for (int j = 0; j < run && x + j < w; j++)
                    {
                        var px = (y * w + x + j) * 4;
                        rgba[px] = r;
                        rgba[px + 1] = g;
                        rgba[px + 2] = b;
                        rgba[px + 3] = 255;
                    }
                }

                x += run;
            }
        }

        return (rgba, w, h);
    }

    /// <summary>
    /// Extract all gumps from gumpart.mul (MUL format) and save as PNG.
    /// Returns number of gumps successfully extracted.
    /// </summary>
    public int ExtractAllGumps(string gumpMulPath, string gumpIdxPath, string outputDir)
    {
        if (!File.Exists(gumpMulPath) || !File.Exists(gumpIdxPath))
            return 0;

        Directory.CreateDirectory(outputDir);

        var mulData = File.ReadAllBytes(gumpMulPath);
        var idxData = File.ReadAllBytes(gumpIdxPath);
        var entryCount = idxData.Length / 12;

        var offsets = new int[entryCount];
        var lengths = new int[entryCount];
        var widths = new int[entryCount];
        var heights = new int[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            offsets[i] = BitConverter.ToInt32(idxData, i * 12);
            lengths[i] = BitConverter.ToInt32(idxData, i * 12 + 4);
            var extra = BitConverter.ToInt32(idxData, i * 12 + 8);
            widths[i] = (extra >> 16) & 0xFFFF;
            heights[i] = extra & 0xFFFF;
        }

        var extracted = 0;
        for (int i = 0; i < entryCount; i++)
        {
            // Skip invalid entries
            if (offsets[i] < 0 || lengths[i] <= 0) continue;
            if (widths[i] <= 0 || heights[i] <= 0) continue;
            if (widths[i] > 2048 || heights[i] > 2048) continue; // Skip oversized gumps

            try
            {
                var sprite = ExtractGumpSprite(i, mulData, offsets, lengths, entryCount, widths, heights);
                if (sprite == null) continue;

                var (rgba, w, h) = sprite.Value;
                var pngPath = Path.Combine(outputDir, $"{i}.png");
                PngWriter.WriteFromRgba(pngPath, w, h, rgba);
                extracted++;
            }
            catch
            {
                // Skip corrupt gumps
            }
        }

        return extracted;
    }

    /// <summary>
    /// Extract all gumps from gumpartLegacyMUL.uop and save as PNG.
    /// </summary>
    public int ExtractAllGumpsFromUop(string uopPath, string outputDir)
    {
        if (!File.Exists(uopPath)) return 0;
        Directory.CreateDirectory(outputDir);

        var uopData = File.ReadAllBytes(uopPath);
        if (uopData.Length < 28 || uopData[0] != 0x4D || uopData[1] != 0x59 || uopData[2] != 0x50)
            return 0;

        // Pattern for gumps in UOP
        var pattern = "build/gumpartlegacymul/{0:D8}.tga";
        var entries = new Dictionary<int, (long offset, int length, int decompLen, bool compressed)>();

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
                var flag = BitConverter.ToInt16(uopData, pos + 32);
                pos += 34;

                if (fileOffset == 0) continue;

                for (int idx = 0; idx < 0x10000; idx++)
                {
                    if (CreateUopHash(string.Format(pattern, idx)) == hash)
                    {
                        entries[idx] = (fileOffset + headerLen, compLen, decompLen, flag == 1);
                        break;
                    }
                }
            }
        }

        var extracted = 0;
        foreach (var (gumpId, (offset, compLen, decompLen, compressed)) in entries.OrderBy(e => e.Key))
        {
            if (offset < 0 || offset >= uopData.Length) continue;

            byte[] data;
            if (compressed && compLen > 0)
            {
                try
                {
                    using var compStream = new System.IO.Compression.DeflateStream(
                        new MemoryStream(uopData, (int)offset + 2, compLen - 2), // Skip zlib header
                        System.IO.Compression.CompressionMode.Decompress);
                    using var outStream = new MemoryStream();
                    compStream.CopyTo(outStream);
                    data = outStream.ToArray();
                }
                catch { continue; }
            }
            else
            {
                var len = decompLen > 0 ? decompLen : compLen;
                if (offset + len > uopData.Length) continue;
                data = new byte[len];
                Array.Copy(uopData, offset, data, 0, len);
            }

            if (data.Length < 8) continue;

            // Gump UOP entry format: width(4) + height(4) + row lookup table + RLE data
            var w = BitConverter.ToInt32(data, 0);
            var h = BitConverter.ToInt32(data, 4);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) continue;

            var rgba = new byte[w * h * 4];

            if (data.Length < 8 + h * 4) continue;

            var rowOffsets = new int[h];
            for (int r = 0; r < h; r++)
                rowOffsets[r] = BitConverter.ToInt32(data, 8 + r * 4);

            for (int y = 0; y < h; y++)
            {
                var rlePos = 8 + rowOffsets[y] * 4;
                var x = 0;
                var nextRowOff = y < h - 1 ? rowOffsets[y + 1] : (data.Length - 8) / 4;
                var gSize = nextRowOff - rowOffsets[y];

                for (int seg = 0; seg < gSize && rlePos + 3 < data.Length; seg++)
                {
                    var color16 = (ushort)(data[rlePos] | (data[rlePos + 1] << 8));
                    var run = (ushort)(data[rlePos + 2] | (data[rlePos + 3] << 8));
                    rlePos += 4;

                    if (color16 != 0)
                    {
                        var cr = ColorLUT[(color16 >> 10) & 0x1F];
                        var cg = ColorLUT[(color16 >> 5) & 0x1F];
                        var cb = ColorLUT[color16 & 0x1F];

                        for (int j = 0; j < run && x + j < w; j++)
                        {
                            var px = (y * w + x + j) * 4;
                            rgba[px] = cr;
                            rgba[px + 1] = cg;
                            rgba[px + 2] = cb;
                            rgba[px + 3] = 255;
                        }
                    }
                    x += run;
                }
            }

            var pngPath = Path.Combine(outputDir, $"{gumpId}.png");
            PngWriter.WriteFromRgba(pngPath, w, h, rgba);
            extracted++;
        }

        return extracted;
    }

    // ---- Art file loading (copied from IsometricRenderer) ----

    private void LoadFromMul(string artIdxPath)
    {
        using var idxStream = new FileStream(artIdxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var idxReader = new BinaryReader(idxStream);

        _artEntryCount = (int)(idxStream.Length / 12);
        _artOffsets = new int[_artEntryCount];
        _artLengths = new int[_artEntryCount];

        for (int i = 0; i < _artEntryCount; i++)
        {
            _artOffsets[i] = (int)idxReader.ReadUInt32();
            _artLengths[i] = idxReader.ReadInt32();
            idxReader.ReadInt32();
        }
    }

    private void LoadFromUop(string uopPath)
    {
        var pattern = "build/artlegacymul/{0:D8}.tga";
        var hashToEntry = new Dictionary<ulong, (long offset, int length)>();

        using var stream = new FileStream(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadUInt32();
        if (magic != 0x50594D) return;

        reader.ReadUInt32();
        reader.ReadUInt32();
        var nextBlock = reader.ReadInt64();
        reader.ReadUInt32();
        reader.ReadInt32();

        stream.Seek(nextBlock, SeekOrigin.Begin);

        do
        {
            var filesCount = reader.ReadInt32();
            nextBlock = reader.ReadInt64();

            for (int i = 0; i < filesCount; i++)
            {
                var fileOffset = reader.ReadInt64();
                var headerLength = reader.ReadInt32();
                var compressedLength = reader.ReadInt32();
                var decompressedLength = reader.ReadInt32();
                var hash = reader.ReadUInt64();
                reader.ReadUInt32();
                var flag = reader.ReadInt16();

                if (fileOffset == 0) continue;

                var length = flag == 1 ? compressedLength : decompressedLength;
                hashToEntry[hash] = (fileOffset + headerLength, length);
            }

            if (nextBlock != 0) stream.Seek(nextBlock, SeekOrigin.Begin);
        } while (nextBlock != 0);

        var maxEntries = Math.Min(hashToEntry.Count + 0x4000, 0x14000);
        _artEntryCount = maxEntries;
        _artOffsets = new int[maxEntries];
        _artLengths = new int[maxEntries];
        Array.Fill(_artOffsets, -1);

        for (int i = 0; i < maxEntries; i++)
        {
            var fileName = string.Format(pattern, i);
            var fileHash = CreateUopHash(fileName);

            if (hashToEntry.TryGetValue(fileHash, out var entry))
            {
                _artOffsets[i] = (int)entry.offset;
                _artLengths[i] = entry.length;
            }
        }
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
}

public class ExportReport
{
    public int DefinitionsSaved { get; set; }
    public int SpritesSaved { get; set; }
    public int SpritesFailed { get; set; }
}
