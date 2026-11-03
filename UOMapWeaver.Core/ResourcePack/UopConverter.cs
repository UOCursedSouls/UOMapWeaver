using System.IO.Compression;

namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Converts UOP files to MUL+IDX format.
/// Based on UOFiddler's LegacyMulFileConverter.FromUop() logic.
///
/// Key insight from UOFiddler: gumps use TWO hash patterns (8-digit and 7-digit)
/// and width/height are in the DECOMPRESSED data, not the UOP header.
/// </summary>
public static class UopConverter
{
    private const int UOP_MAGIC = 0x50594D; // "MYP"

    public enum FileType
    {
        Art,
        Gumpart,
        Sound,
        Map
    }

    /// <summary>
    /// Convert a UOP file to MUL+IDX pair.
    /// Follows UOFiddler's LegacyMulFileConverter.FromUop() exactly.
    /// </summary>
    public static int Convert(string uopPath, string mulPath, string idxPath, FileType type)
    {
        var (patterns, maxId) = GetHashFormats(type);

        // Build hash → index lookup tables (same as UOFiddler)
        var chunkIds = new Dictionary<ulong, int>();
        for (int i = 0; i < maxId; i++)
            chunkIds[CreateHash(string.Format(patterns[0], i))] = i;

        if (patterns.Length > 1 && !string.IsNullOrEmpty(patterns[1]))
        {
            for (int i = 0; i < maxId; i++)
            {
                var hash = CreateHash(string.Format(patterns[1], i));
                chunkIds.TryAdd(hash, i);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(mulPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(idxPath)!);

        using var reader = new BinaryReader(new FileStream(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        using var mulWriter = new BinaryWriter(new FileStream(mulPath, FileMode.Create));
        using var idxWriter = new BinaryWriter(new FileStream(idxPath, FileMode.Create));

        if (reader.ReadInt32() != UOP_MAGIC)
            throw new ArgumentException("Not a UOP file");

        reader.ReadInt32(); // version
        reader.ReadInt32(); // timestamp
        var nextTable = reader.ReadInt64();
        reader.ReadInt32(); // block size
        reader.ReadInt32(); // count

        var used = new bool[maxId];
        var converted = 0;

        do
        {
            reader.BaseStream.Seek(nextTable, SeekOrigin.Begin);
            var entries = reader.ReadInt32();
            nextTable = reader.ReadInt64();

            var tableEntries = new (long offset, int headerLen, int compSize, int decompSize, ulong hash, short flag)[entries];

            for (int i = 0; i < entries; i++)
            {
                tableEntries[i].offset = reader.ReadInt64();
                tableEntries[i].headerLen = reader.ReadInt32();
                tableEntries[i].compSize = reader.ReadInt32();
                tableEntries[i].decompSize = reader.ReadInt32();
                tableEntries[i].hash = reader.ReadUInt64();
                reader.ReadUInt32(); // data hash
                tableEntries[i].flag = reader.ReadInt16();
            }

            foreach (var entry in tableEntries)
            {
                if (entry.offset == 0) continue;

                if (!chunkIds.TryGetValue(entry.hash, out var chunkId))
                    continue;

                // Read data
                reader.BaseStream.Seek(entry.offset + entry.headerLen, SeekOrigin.Begin);
                var chunkData = reader.ReadBytes(entry.compSize);

                // Decompress if needed
                if (entry.flag != 0)
                {
                    // Step 1: Zlib decompress
                    byte[] zlibDecompressed;
                    try
                    {
                        using var zlib = new ZLibStream(new MemoryStream(chunkData), CompressionMode.Decompress);
                        var decompBuf = new byte[entry.decompSize > 0 ? entry.decompSize : chunkData.Length * 4];
                        using var ms = new MemoryStream();
                        zlib.CopyTo(ms);
                        zlibDecompressed = ms.ToArray();
                    }
                    catch
                    {
                        try
                        {
                            using var deflate = new DeflateStream(
                                new MemoryStream(chunkData, 2, chunkData.Length - 2),
                                CompressionMode.Decompress);
                            using var ms = new MemoryStream();
                            deflate.CopyTo(ms);
                            zlibDecompressed = ms.ToArray();
                        }
                        catch { continue; }
                    }

                    // Step 2: If flag=3 (ZlibBwt/Mythic), apply BWT decompression
                    if (entry.flag == 3)
                    {
                        try
                        {
                            chunkData = MythicDecompress.Decompress(zlibDecompressed);
                        }
                        catch
                        {
                            chunkData = zlibDecompressed; // Fallback
                        }
                    }
                    else
                    {
                        chunkData = zlibDecompressed;
                    }
                }

                // Write IDX entry at correct position
                idxWriter.Seek(chunkId * 12, SeekOrigin.Begin);
                idxWriter.Write((int)mulWriter.BaseStream.Position); // offset into MUL

                int dataOffset = 0;

                switch (type)
                {
                    case FileType.Gumpart:
                    {
                        // Width/Height are FIRST 8 bytes of decompressed data
                        if (chunkData.Length < 8) continue;
                        int width = chunkData[0] | (chunkData[1] << 8) | (chunkData[2] << 16) | (chunkData[3] << 24);
                        int height = chunkData[4] | (chunkData[5] << 8) | (chunkData[6] << 16) | (chunkData[7] << 24);

                        idxWriter.Write(chunkData.Length - 8); // length without w/h header
                        idxWriter.Write((width << 16) | height); // extra = (w<<16)|h
                        dataOffset = 8; // skip width/height in MUL data
                        break;
                    }
                    case FileType.Sound:
                    {
                        idxWriter.Write(chunkData.Length);
                        idxWriter.Write(chunkId + 1); // extra = sound ID + 1
                        break;
                    }
                    default:
                    {
                        idxWriter.Write(chunkData.Length);
                        idxWriter.Write(0); // extra = 0
                        break;
                    }
                }

                // Write MUL data (skip dataOffset bytes for gumps)
                mulWriter.Write(chunkData, dataOffset, chunkData.Length - dataOffset);
                used[chunkId] = true;
                converted++;
            }

            if (nextTable != 0)
                reader.BaseStream.Seek(nextTable, SeekOrigin.Begin);

        } while (nextTable != 0);

        // Fill unused IDX entries with -1
        for (int i = 0; i < maxId; i++)
        {
            if (!used[i])
            {
                idxWriter.Seek(i * 12, SeekOrigin.Begin);
                idxWriter.Write(-1); // offset = -1
                idxWriter.Write(0);
                idxWriter.Write(0);
            }
        }

        return converted;
    }

    private static (string[] patterns, int maxId) GetHashFormats(FileType type)
    {
        return type switch
        {
            FileType.Art => (new[] { "build/artlegacymul/{0:00000000}.tga" }, 0x13FDC),
            // Gumps: TWO patterns — 8-digit and 7-digit (UOFiddler compatibility)
            FileType.Gumpart => (new[] { "build/gumpartlegacymul/{0:00000000}.tga", "build/gumpartlegacymul/{0:0000000}.tga" }, 0x10000),
            FileType.Sound => (new[] { "build/soundlegacymul/{0:00000000}.dat" }, 0xFFF),
            _ => (new[] { "build/artlegacymul/{0:00000000}.tga" }, 0x10000)
        };
    }

    // Same hash function as UOFiddler/ClassicUO (HashLittle2)
    public static ulong CreateHash(string s)
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
