using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace UOMapWeaver.Core.Uop;

public static class UopCodec
{
    public const uint Magic = 0x0050594D;
    private const int DefaultDataOffset = 0x200;
    private const int DefaultBlockCapacity = 1000;

    public static UopArchive Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidOperationException("Invalid UOP header.");
        }

        var version = reader.ReadUInt32();
        var timestamp = reader.ReadUInt32();
        var nextBlockOffset = reader.ReadInt64();
        var blockCapacity = reader.ReadInt32();
        var fileCount = reader.ReadInt32();
        var unknown1 = reader.ReadInt32();
        var unknown2 = reader.ReadInt32();

        var entries = new List<UopEntry>();
        var blockOffset = nextBlockOffset;
        var entryIndex = 0;

        while (blockOffset != 0)
        {
            stream.Seek(blockOffset, SeekOrigin.Begin);
            var blockEntries = reader.ReadInt32();
            var nextBlock = reader.ReadInt64();

            for (var i = 0; i < blockEntries; i++)
            {
                var offset = reader.ReadInt64();
                var headerLength = reader.ReadInt32();
                var compressedLength = reader.ReadInt32();
                var decompressedLength = reader.ReadInt32();
                var hash = reader.ReadInt64();
                var crc = reader.ReadInt32();
                var flags = reader.ReadUInt16();

                entries.Add(new UopEntry(
                    entryIndex++,
                    offset,
                    headerLength,
                    compressedLength,
                    decompressedLength,
                    hash,
                    crc,
                    flags));
            }

            blockOffset = nextBlock;
        }

        return new UopArchive(
            path,
            version,
            timestamp,
            nextBlockOffset,
            blockCapacity,
            fileCount,
            unknown1,
            unknown2,
            entries);
    }

    public static UopPackResult ExtractToFile(
        string uopPath,
        string outputPath,
        bool combineEntries,
        Action<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken? token = null)
    {
        var archive = Read(uopPath);
        var entries = archive.Entries.Where(entry => entry.Offset > 0 && entry.DecompressedLength > 0)
            .OrderBy(entry => entry.Index)
            .ToList();

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("No entries found in UOP.");
        }

        using var stream = new FileStream(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var total = entries.Count;

        if (combineEntries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

            for (var i = 0; i < entries.Count; i++)
            {
                token?.ThrowIfCancellationRequested();
                var data = ReadEntryData(stream, entries[i]);
                output.Write(data, 0, data.Length);
                ReportProgress(progress, i + 1, total);
            }
        }
        else
        {
            Directory.CreateDirectory(outputPath);
            for (var i = 0; i < entries.Count; i++)
            {
                token?.ThrowIfCancellationRequested();
                var entry = entries[i];
                var data = ReadEntryData(stream, entry);
                var name = $"entry_{entry.Index:D4}.bin";
                var target = Path.Combine(outputPath, name);
                File.WriteAllBytes(target, data);
                ReportProgress(progress, i + 1, total);
            }
        }

        log?.Invoke($"Extracted {entries.Count} entries from {Path.GetFileName(uopPath)}.");
        return new UopPackResult(entries.Count, entries.Sum(e => (long)e.DecompressedLength));
    }

    public static UopPackResult PackFile(
        string inputPath,
        string outputPath,
        string? templatePath,
        int chunkSize,
        Action<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken? token = null)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file not found.", inputPath);
        }

        var inputBytes = File.ReadAllBytes(inputPath);
        var template = string.IsNullOrWhiteSpace(templatePath) ? null : Read(templatePath!);
        var templateEntries = template?.Entries.Where(entry => entry.Offset > 0 && entry.DecompressedLength > 0)
            .OrderBy(entry => entry.Index)
            .ToList();

        var entrySizes = templateEntries?.Select(entry => entry.DecompressedLength).ToList();
        if (entrySizes is null || entrySizes.Count == 0)
        {
            if (chunkSize <= 0)
            {
                chunkSize = 0xC4000;
            }

            entrySizes = new List<int>();
            var remaining = inputBytes.Length;
            while (remaining > 0)
            {
                var size = Math.Min(chunkSize, remaining);
                entrySizes.Add(size);
                remaining -= size;
            }
        }

        var chunkCount = entrySizes.Count;
        var maxCapacity = template?.BlockCapacity > 0 ? template.BlockCapacity : DefaultBlockCapacity;
        if (chunkCount > maxCapacity)
        {
            throw new InvalidOperationException($"Input requires {chunkCount} chunks but UOP capacity is {maxCapacity}.");
        }

        var entries = new List<UopEntry>(maxCapacity);
        var dataOffset = templateEntries?.Min(entry => entry.Offset) ?? DefaultDataOffset;
        var headerBytes = templateEntries?.Select(entry => ReadEntryHeader(template!.Path, entry)).ToList();

        var inputOffset = 0;
        var writtenChunks = 0;

        for (var i = 0; i < maxCapacity; i++)
        {
            if (i < entrySizes.Count && inputOffset < inputBytes.Length)
            {
                var size = entrySizes[i];
                if (inputOffset + size > inputBytes.Length)
                {
                    size = inputBytes.Length - inputOffset;
                }

                var templateEntry = templateEntries is not null && i < templateEntries.Count ? templateEntries[i] : null;
                var entry = new UopEntry(
                    i,
                    dataOffset,
                    templateEntry?.HeaderLength ?? 0,
                    size,
                    size,
                    templateEntry?.Hash ?? 0,
                    0,
                    0);
                entries.Add(entry);
                dataOffset += entry.HeaderLength + size;
                inputOffset += size;
                writtenChunks++;
            }
            else
            {
                entries.Add(new UopEntry(i, 0, 0, 0, 0, 0, 0, 0));
            }
        }

        var header = new UopArchive(
            outputPath,
            template?.Version ?? 5,
            template?.Timestamp ?? 0,
            0,
            maxCapacity,
            writtenChunks,
            template?.Unknown1 ?? 1,
            template?.Unknown2 ?? 1,
            entries);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output);

        WriteHeader(writer, header, 0);
        if (output.Position < dataOffset)
        {
            output.Write(new byte[dataOffset - output.Position]);
        }

        inputOffset = 0;
        for (var i = 0; i < writtenChunks; i++)
        {
            token?.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.Offset <= 0 || entry.CompressedLength <= 0)
            {
                continue;
            }

            output.Seek(entry.Offset, SeekOrigin.Begin);
            var headerData = headerBytes is not null && i < headerBytes.Count ? headerBytes[i] : Array.Empty<byte>();
            if (headerData.Length > 0)
            {
                output.Write(headerData, 0, headerData.Length);
            }
            else if (entry.HeaderLength > 0)
            {
                output.Write(new byte[entry.HeaderLength]);
            }

            var size = entry.CompressedLength;
            if (inputOffset + size > inputBytes.Length)
            {
                size = inputBytes.Length - inputOffset;
            }

            output.Write(inputBytes, inputOffset, size);
            inputOffset += size;
            ReportProgress(progress, i + 1, writtenChunks);
        }

        var blockOffset = output.Length;
        output.Seek(blockOffset, SeekOrigin.Begin);
        writer.Write(maxCapacity);
        writer.Write((long)0);

        foreach (var entry in entries)
        {
            writer.Write(entry.Offset);
            writer.Write(entry.HeaderLength);
            writer.Write(entry.CompressedLength);
            writer.Write(entry.DecompressedLength);
            writer.Write(entry.Hash);
            writer.Write(entry.Crc);
            writer.Write(entry.Flags);
        }

        output.Seek(0, SeekOrigin.Begin);
        WriteHeader(writer, header, blockOffset);

        log?.Invoke($"Packed {writtenChunks} entries into {Path.GetFileName(outputPath)}.");
        return new UopPackResult(writtenChunks, inputBytes.Length);
    }

    private static void WriteHeader(BinaryWriter writer, UopArchive header, long blockOffset)
    {
        writer.Write(Magic);
        writer.Write(header.Version);
        writer.Write(header.Timestamp);
        writer.Write(blockOffset);
        writer.Write(header.BlockCapacity);
        writer.Write(header.FileCount);
        writer.Write(header.Unknown1);
        writer.Write(header.Unknown2);
    }

    private static byte[] ReadEntryData(FileStream stream, UopEntry entry)
    {
        if (entry.Offset <= 0 || entry.CompressedLength <= 0)
        {
            return Array.Empty<byte>();
        }

        stream.Seek(entry.Offset + entry.HeaderLength, SeekOrigin.Begin);
        var buffer = new byte[entry.CompressedLength];
        stream.ReadExactly(buffer);

        if (IsCompressed(entry))
        {
            return Decompress(buffer, entry.DecompressedLength);
        }

        return buffer;
    }

    private static byte[]? ReadEntryHeader(string uopPath, UopEntry entry)
    {
        if (entry.Offset <= 0 || entry.HeaderLength <= 0)
        {
            return null;
        }

        using var stream = new FileStream(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(entry.Offset, SeekOrigin.Begin);
        var buffer = new byte[entry.HeaderLength];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static bool IsCompressed(UopEntry entry)
        => entry.Flags != 0 || entry.CompressedLength != entry.DecompressedLength;

    private static byte[] Decompress(byte[] input, int expectedSize)
    {
        try
        {
            using var inputStream = new MemoryStream(input);
            using var zlib = new ZLibStream(inputStream, CompressionMode.Decompress);
            using var output = new MemoryStream(expectedSize > 0 ? expectedSize : input.Length * 2);
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            using var inputStream = new MemoryStream(input);
            using var deflate = new DeflateStream(inputStream, CompressionMode.Decompress);
            using var output = new MemoryStream(expectedSize > 0 ? expectedSize : input.Length * 2);
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }

    private static void ReportProgress(IProgress<int>? progress, int completed, int total)
    {
        if (progress is null || total <= 0)
        {
            return;
        }

        var percent = (int)(completed * 100.0 / total);
        progress.Report(percent);
    }
}

public sealed record UopArchive(
    string Path,
    uint Version,
    uint Timestamp,
    long NextBlockOffset,
    int BlockCapacity,
    int FileCount,
    int Unknown1,
    int Unknown2,
    IReadOnlyList<UopEntry> Entries);

public sealed record UopEntry(
    int Index,
    long Offset,
    int HeaderLength,
    int CompressedLength,
    int DecompressedLength,
    long Hash,
    int Crc,
    ushort Flags);

public sealed record UopPackResult(int EntryCount, long TotalBytes);
