using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Linq;

namespace UOMapWeaver.Core.Uop;

public static class UopCodec
{
    public const uint Magic = 0x0050594D;
    private const uint DefaultSignature = 0xFD23EC43;
    private const int DefaultDataOffset = 0x200;
    private const int DefaultBlockCapacity = 1000;
    private const int MapChunkSize = 0xC4000;

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

    public static UopPackResult ExtractLegacyUopToMul(
        string uopPath,
        string outputFolder,
        string prefix,
        LegacyUopType type,
        int typeIndex,
        Action<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken? token = null)
    {
        var archive = Read(uopPath);
        var entries = archive.Entries.Where(entry => entry.Offset > 0 && entry.DecompressedLength > 0)
            .ToList();

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("No entries found in UOP.");
        }

        Directory.CreateDirectory(outputFolder);

        var (mulName, idxName) = GetLegacyOutputNames(prefix, type, typeIndex);
        var mulPath = Path.Combine(outputFolder, mulName);
        var idxPath = idxName is null ? null : Path.Combine(outputFolder, idxName);
        var housingPath = type == LegacyUopType.MultiCollection
            ? Path.Combine(outputFolder, $"{prefix}housing.bin")
            : null;

        using var source = new FileStream(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var mulStream = new FileStream(mulPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var idxStream = idxPath is null ? null : new FileStream(idxPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var idxWriter = idxStream is null ? null : new BinaryWriter(idxStream);

        var hashLookup = entries.ToDictionary(entry => (ulong)entry.Hash, entry => entry);
        var (pattern, altPattern, maxId) = GetHashPattern(type, typeIndex, entries);
        var used = idxWriter is null ? null : new bool[maxId];

        var written = 0;
        for (var id = 0; id < maxId; id++)
        {
            token?.ThrowIfCancellationRequested();
            var hash = HashEntryName(string.Format(CultureInfo.InvariantCulture, pattern, id));
            if (!hashLookup.TryGetValue(hash, out var entry) && !string.IsNullOrWhiteSpace(altPattern))
            {
                var altHash = HashEntryName(string.Format(CultureInfo.InvariantCulture, altPattern, id));
                hashLookup.TryGetValue(altHash, out entry);
            }

            if (entry is null)
            {
                if (idxWriter is not null)
                {
                    idxWriter.Write(-1);
                    idxWriter.Write(0);
                    idxWriter.Write(0);
                }
                continue;
            }

            var data = ReadEntryData(source, entry);
            if (type == LegacyUopType.Map)
            {
                mulStream.Seek(id * (long)MapChunkSize, SeekOrigin.Begin);
                mulStream.Write(data, 0, data.Length);
                written++;
                ReportProgress(progress, id + 1, maxId);
                continue;
            }

            if (type == LegacyUopType.MultiCollection && entry.Hash == (long)HousingBinHash && housingPath is not null)
            {
                File.WriteAllBytes(housingPath, data);
                written++;
                continue;
            }

            if (idxWriter is null)
            {
                mulStream.Write(data, 0, data.Length);
                written++;
                continue;
            }

            idxWriter.Seek(id * 12, SeekOrigin.Begin);
            idxWriter.Write((int)mulStream.Position);

            var dataOffset = 0;
            if (type == LegacyUopType.Gump)
            {
                if (data.Length >= 8)
                {
                    var width = BitConverter.ToInt32(data, 0);
                    var height = BitConverter.ToInt32(data, 4);
                    idxWriter.Write(data.Length - 8);
                    idxWriter.Write((width << 16) | (height & 0xFFFF));
                    dataOffset = 8;
                }
                else
                {
                    idxWriter.Write(data.Length);
                    idxWriter.Write(0);
                }
            }
            else if (type == LegacyUopType.Sound)
            {
                idxWriter.Write(data.Length);
                idxWriter.Write(id + 1);
            }
            else if (type == LegacyUopType.MultiCollection)
            {
                var before = mulStream.Position;
                WriteMultiEntry(mulStream, data);
                var after = mulStream.Position;
                idxWriter.Write((int)(after - before));
                idxWriter.Write(0);
                if (used is not null)
                {
                    used[id] = true;
                }
                written++;
                ReportProgress(progress, id + 1, maxId);
                continue;
            }
            else
            {
                idxWriter.Write(data.Length);
                idxWriter.Write(0);
            }

            if (used is not null)
            {
                used[id] = true;
            }

            mulStream.Write(data, dataOffset, data.Length - dataOffset);
            written++;
            ReportProgress(progress, id + 1, maxId);
        }

        if (idxWriter is not null && used is not null)
        {
            for (var i = 0; i < used.Length; i++)
            {
                if (used[i])
                {
                    continue;
                }

                idxWriter.Seek(i * 12, SeekOrigin.Begin);
                idxWriter.Write(-1);
                idxWriter.Write(0);
                idxWriter.Write(0);
            }
        }

        log?.Invoke($"Extracted {written} entries to {mulName}{(idxName is null ? string.Empty : $" + {idxName}")}.");
        return new UopPackResult(written, mulStream.Length);
    }

    public static UopPackResult ExtractToSegments(
        string uopPath,
        IReadOnlyList<UopOutputSegment> segments,
        Action<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken? token = null)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("No output segments provided.");
        }

        var archive = Read(uopPath);
        var entries = archive.Entries.Where(entry => entry.Offset > 0 && entry.DecompressedLength > 0)
            .OrderBy(entry => entry.Index)
            .ToList();

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("No entries found in UOP.");
        }

        foreach (var segment in segments)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(segment.Path) ?? ".");
        }

        using var source = new FileStream(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var writers = segments.Select(segment => new FileStream(segment.Path, FileMode.Create, FileAccess.Write, FileShare.None)).ToList();
        try
        {
            var total = entries.Count;
            var segmentIndex = 0;
            var remainingInSegment = segments[segmentIndex].Length;
            var written = 0L;

            for (var i = 0; i < entries.Count; i++)
            {
                token?.ThrowIfCancellationRequested();
                var data = ReadEntryData(source, entries[i]);
                var dataOffset = 0;
                var remainingData = data.Length;

                while (remainingData > 0 && segmentIndex < segments.Count)
                {
                    var toWrite = (int)Math.Min(remainingData, remainingInSegment);
                    if (toWrite > 0)
                    {
                        writers[segmentIndex].Write(data, dataOffset, toWrite);
                        written += toWrite;
                    }

                    remainingData -= toWrite;
                    dataOffset += toWrite;
                    remainingInSegment -= toWrite;

                    if (remainingInSegment == 0 && segmentIndex + 1 < segments.Count)
                    {
                        segmentIndex++;
                        remainingInSegment = segments[segmentIndex].Length;
                    }
                    else if (remainingInSegment == 0)
                    {
                        break;
                    }
                }

                ReportProgress(progress, i + 1, total);
            }

            log?.Invoke($"Extracted {entries.Count} entries into {segments.Count} segments.");
            return new UopPackResult(entries.Count, written);
        }
        finally
        {
            foreach (var writer in writers)
            {
                writer.Dispose();
            }
        }
    }

    public static UopPackResult ExtractIndexedToFiles(
        string uopPath,
        string outputFolder,
        string baseName,
        Action<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken? token = null)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new InvalidOperationException("Base name is required.");
        }

        var archive = Read(uopPath);
        var ordered = archive.Entries.OrderBy(entry => entry.Index).ToList();
        if (ordered.Count == 0)
        {
            throw new InvalidOperationException("No entries found in UOP.");
        }

        Directory.CreateDirectory(outputFolder);
        var (mulName, idxName) = GetMulIdxNames(baseName);
        var mulPath = Path.Combine(outputFolder, mulName);
        var idxPath = Path.Combine(outputFolder, idxName);

        using var source = new FileStream(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var mulStream = new FileStream(mulPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var idxStream = new FileStream(idxPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var idxWriter = new BinaryWriter(idxStream);

        var written = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            token?.ThrowIfCancellationRequested();
            var entry = ordered[i];
            if (entry.Offset <= 0 || entry.DecompressedLength <= 0)
            {
                idxWriter.Write(-1);
                idxWriter.Write(0);
                idxWriter.Write(0);
                continue;
            }

            var data = ReadEntryData(source, entry);
            var offset = (int)mulStream.Position;
            mulStream.Write(data, 0, data.Length);
            idxWriter.Write(offset);
            idxWriter.Write(data.Length);
            idxWriter.Write(0);
            written++;
            ReportProgress(progress, i + 1, ordered.Count);
        }

        log?.Invoke($"Extracted {written} indexed entries to {mulName} / {idxName}.");
        return new UopPackResult(written, mulStream.Length);
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
        var headerBytes = templateEntries is not null && template is not null
            ? templateEntries.Select(entry => ReadEntryHeader(template.Path, entry) ?? Array.Empty<byte>()).ToList()
            : null;

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
            template?.Timestamp ?? DefaultSignature,
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

    public static UopPackResult PackIndexedFromFiles(
        string mulPath,
        string idxPath,
        string outputPath,
        string templatePath,
        Action<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken? token = null)
    {
        if (!File.Exists(mulPath))
        {
            throw new FileNotFoundException("Mul file not found.", mulPath);
        }

        if (!File.Exists(idxPath))
        {
            throw new FileNotFoundException("Idx file not found.", idxPath);
        }

        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            throw new FileNotFoundException("Template UOP is required.", templatePath);
        }

        var template = Read(templatePath);
        var templateEntries = template.Entries.OrderBy(entry => entry.Index).ToList();
        var headerBytes = templateEntries.Select(entry => ReadEntryHeader(template.Path, entry) ?? Array.Empty<byte>()).ToList();

        var idxEntries = ReadIdxEntries(idxPath);
        if (idxEntries.Count > templateEntries.Count)
        {
            throw new InvalidOperationException($"IDX has {idxEntries.Count} entries but template supports {templateEntries.Count}.");
        }

        var entries = new List<UopEntry>(templateEntries.Count);
        var dataOffset = templateEntries.Where(entry => entry.Offset > 0).Select(entry => entry.Offset).DefaultIfEmpty(DefaultDataOffset).Min();
        var writtenChunks = 0;

        using var mulStream = new FileStream(mulPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        for (var i = 0; i < templateEntries.Count; i++)
        {
            if (i < idxEntries.Count && idxEntries[i].Offset >= 0 && idxEntries[i].Length > 0)
            {
                var idx = idxEntries[i];
                var templateEntry = templateEntries[i];
                var entry = new UopEntry(
                    i,
                    dataOffset,
                    templateEntry.HeaderLength,
                    idx.Length,
                    idx.Length,
                    templateEntry.Hash,
                    0,
                    0);
                entries.Add(entry);
                dataOffset += entry.HeaderLength + entry.CompressedLength;
                writtenChunks++;
            }
            else
            {
                entries.Add(new UopEntry(i, 0, 0, 0, 0, 0, 0, 0));
            }
        }

        var header = new UopArchive(
            outputPath,
            template.Version,
            template.Timestamp,
            0,
            template.BlockCapacity,
            writtenChunks,
            template.Unknown1,
            template.Unknown2,
            entries);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output);
        WriteHeader(writer, header, 0);

        if (output.Position < dataOffset)
        {
            output.Write(new byte[dataOffset - output.Position]);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            token?.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.Offset <= 0 || entry.CompressedLength <= 0)
            {
                continue;
            }

            var idx = idxEntries[i];
            output.Seek(entry.Offset, SeekOrigin.Begin);
            var headerData = i < headerBytes.Count ? headerBytes[i] : Array.Empty<byte>();
            if (headerData.Length > 0)
            {
                output.Write(headerData, 0, headerData.Length);
            }
            else if (entry.HeaderLength > 0)
            {
                output.Write(new byte[entry.HeaderLength]);
            }

            mulStream.Seek(idx.Offset, SeekOrigin.Begin);
            var buffer = new byte[idx.Length];
            mulStream.ReadExactly(buffer);
            output.Write(buffer, 0, buffer.Length);
            ReportProgress(progress, i + 1, entries.Count);
        }

        var blockOffset = output.Length;
        output.Seek(blockOffset, SeekOrigin.Begin);
        writer.Write(template.BlockCapacity);
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

        log?.Invoke($"Packed {writtenChunks} indexed entries into {Path.GetFileName(outputPath)}.");
        return new UopPackResult(writtenChunks, (int)mulStream.Length);
    }

    public static UopPackResult PackLegacyUopFromMul(
        string mulPath,
        int mapIndex,
        string outputPath,
        Action<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken? token = null)
    {
        if (!File.Exists(mulPath))
        {
            throw new FileNotFoundException("Mul file not found.", mulPath);
        }

        var entries = new List<UopEntryData>();
        var seen = new HashSet<ulong>();
        using var mulStream = new FileStream(mulPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var chunkIndex = 0;
        var buffer = new byte[MapChunkSize];
        while (mulStream.Position < mulStream.Length)
        {
            token?.ThrowIfCancellationRequested();
            var read = mulStream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var data = read == buffer.Length ? buffer.ToArray() : buffer[..read].ToArray();
            var hash = HashEntryName(string.Format(CultureInfo.InvariantCulture, $"build/map{mapIndex}legacymul/{{0:00000000}}.dat", chunkIndex));
            if (hash == 0 || !seen.Add(hash))
            {
                log?.Invoke($"Skipped duplicate/zero hash for chunk {chunkIndex}.");
                chunkIndex++;
                continue;
            }

            entries.Add(new UopEntryData(chunkIndex, data, hash));
            chunkIndex++;
            ReportProgress(progress, chunkIndex, (int)Math.Ceiling(mulStream.Length / (double)MapChunkSize));
        }

        return WriteLegacyUop(outputPath, entries, log);
    }

    public static UopPackResult PackLegacyUopFromMulIdx(
        string mulPath,
        string idxPath,
        LegacyUopType type,
        int mapIndex,
        string outputPath,
        Action<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken? token = null,
        string? housingBinPath = null)
    {
        if (!File.Exists(mulPath))
        {
            throw new FileNotFoundException("Mul file not found.", mulPath);
        }

        if (!File.Exists(idxPath))
        {
            throw new FileNotFoundException("Idx file not found.", idxPath);
        }

        var idxEntries = ReadIdxEntries(idxPath);
        var (pattern, altPattern, _) = GetHashPattern(type, mapIndex, Array.Empty<UopEntry>());

        var entries = new List<UopEntryData>();
        var seen = new HashSet<ulong>();
        using var mulStream = new FileStream(mulPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        for (var i = 0; i < idxEntries.Count; i++)
        {
            token?.ThrowIfCancellationRequested();
            var idx = idxEntries[i];
            if (idx.Offset < 0 || idx.Length <= 0)
            {
                continue;
            }

            mulStream.Seek(idx.Offset, SeekOrigin.Begin);
            var data = new byte[idx.Length];
            mulStream.ReadExactly(data);

            if (type == LegacyUopType.Gump)
            {
                var width = (idx.Extra >> 16) & 0xFFFF;
                var height = idx.Extra & 0xFFFF;
                var gumpData = new byte[data.Length + 8];
                BitConverter.GetBytes(width).CopyTo(gumpData, 0);
                BitConverter.GetBytes(height).CopyTo(gumpData, 4);
                Array.Copy(data, 0, gumpData, 8, data.Length);
                data = gumpData;
            }

            var hash = HashEntryName(string.Format(CultureInfo.InvariantCulture, pattern, i));
            if (hash == 0 && !string.IsNullOrWhiteSpace(altPattern))
            {
                hash = HashEntryName(string.Format(CultureInfo.InvariantCulture, altPattern, i));
            }

            if (hash == 0 || !seen.Add(hash))
            {
                log?.Invoke($"Skipped duplicate/zero hash for entry {i}.");
                continue;
            }

            entries.Add(new UopEntryData(i, data, hash));
            ReportProgress(progress, i + 1, idxEntries.Count);
        }

        if (type == LegacyUopType.MultiCollection && !string.IsNullOrWhiteSpace(housingBinPath) && File.Exists(housingBinPath))
        {
            var housingData = File.ReadAllBytes(housingBinPath);
            if (seen.Add(HousingBinHash))
            {
                entries.Add(new UopEntryData(entries.Count, housingData, HousingBinHash));
            }
            else
            {
                log?.Invoke("Skipped duplicate housing.bin hash.");
            }
        }

        return WriteLegacyUop(outputPath, entries, log);
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

    private static UopPackResult WriteLegacyUop(string outputPath, IReadOnlyList<UopEntryData> entries, Action<string>? log)
    {
        var filtered = new List<UopEntryData>(entries.Count);
        var seen = new HashSet<ulong>();
        var skippedZero = 0;
        var skippedDup = 0;

        foreach (var entry in entries)
        {
            if (entry.Hash == 0)
            {
                skippedZero++;
                continue;
            }

            if (!seen.Add(entry.Hash))
            {
                skippedDup++;
                continue;
            }

            filtered.Add(entry);
        }

        var fileCount = filtered.Count;
        var blockCapacity = fileCount;
        var dataOffset = DefaultDataOffset;
        var offsets = new List<UopEntry>(blockCapacity);

        var dataCursor = dataOffset;
        for (var i = 0; i < fileCount; i++)
        {
            var data = filtered[i];
            offsets.Add(new UopEntry(
                data.Index,
                dataCursor,
                0,
                data.Data.Length,
                data.Data.Length,
                (long)data.Hash,
                0,
                0));
            dataCursor += data.Data.Length;
        }

        if (blockCapacity == 0)
        {
            throw new InvalidOperationException("No entries to write.");
        }

        var header = new UopArchive(
            outputPath,
            5,
            DefaultSignature,
            0,
            blockCapacity,
            fileCount,
            0,
            0,
            offsets);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output);

        WriteHeader(writer, header, 0);
        if (output.Position < dataOffset)
        {
            output.Write(new byte[dataOffset - output.Position]);
        }

        for (var i = 0; i < fileCount; i++)
        {
            var entry = offsets[i];
            output.Seek(entry.Offset, SeekOrigin.Begin);
            output.Write(filtered[i].Data, 0, filtered[i].Data.Length);
        }

        var blockOffset = output.Length;
        output.Seek(blockOffset, SeekOrigin.Begin);
        writer.Write(blockCapacity);
        writer.Write((long)0);

        foreach (var entry in offsets)
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

        log?.Invoke($"UOP table entries: {fileCount}");

        if (skippedZero > 0 || skippedDup > 0)
        {
            log?.Invoke($"Skipped entries: zero hash={skippedZero}, duplicate hash={skippedDup}.");
        }

        log?.Invoke($"Packed {fileCount} entries into {Path.GetFileName(outputPath)}.");
        return new UopPackResult(fileCount, output.Length);
    }

    private static (string MulName, string IdxName) GetMulIdxNames(string baseName)
    {
        var name = baseName.Trim();
        switch (name.ToLowerInvariant())
        {
            case "gumpart":
                return ("gumpart.mul", "gumpidx.mul");
            case "art":
                return ("art.mul", "artidx.mul");
            case "sound":
                return ("sound.mul", "soundidx.mul");
            default:
                return ($"{name}.mul", $"{name}idx.mul");
        }
    }

    private static List<(int Offset, int Length, int Extra)> ReadIdxEntries(string idxPath)
    {
        var entries = new List<(int Offset, int Length, int Extra)>();
        using var stream = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        while (stream.Position + 12 <= stream.Length)
        {
            var offset = reader.ReadInt32();
            var length = reader.ReadInt32();
            var extra = reader.ReadInt32();
            entries.Add((offset, length, extra));
        }

        return entries;
    }

    private static (string MulName, string? IdxName) GetLegacyOutputNames(string prefix, LegacyUopType type, int typeIndex)
    {
        return type switch
        {
            LegacyUopType.Art => ($"{prefix}art.mul", $"{prefix}artidx.mul"),
            LegacyUopType.Gump => ($"{prefix}gumpart.mul", $"{prefix}gumpidx.mul"),
            LegacyUopType.Sound => ($"{prefix}sound.mul", $"{prefix}soundidx.mul"),
            LegacyUopType.MultiCollection => ($"{prefix}multicollection.mul", $"{prefix}multicollection.idx"),
            LegacyUopType.Map => ($"{prefix}map{typeIndex}.mul", null),
            _ => ($"{prefix}uop.mul", null)
        };
    }

    private static (string Pattern, string? AltPattern, int MaxId) GetHashPattern(
        LegacyUopType type,
        int typeIndex,
        IReadOnlyCollection<UopEntry> entries)
    {
        return type switch
        {
            LegacyUopType.Art => ("build/artlegacymul/{0:00000000}.tga", null, 0x13FDC),
            LegacyUopType.Gump => ("build/gumpartlegacymul/{0:00000000}.tga", "build/gumpartlegacymul/{0:0000000}.tga", 0xFFFF),
            LegacyUopType.Sound => ("build/soundlegacymul/{0:00000000}.dat", null, 0x10000),
            LegacyUopType.MultiCollection => ("build/multicollection/{0:000000}.bin", null, 0x2200),
            LegacyUopType.Map => ($"build/map{typeIndex}legacymul/{{0:00000000}}.dat", null, GuessMapChunkCount(typeIndex, entries)),
            _ => ("build/unknown/{0:00000000}.dat", null, entries.Count)
        };
    }

    private static int GuessMapChunkCount(int typeIndex, IReadOnlyCollection<UopEntry> entries)
    {
        var total = entries.Sum(entry => (long)entry.DecompressedLength);
        if (total > 0)
        {
            var count = (int)Math.Ceiling(total / (double)MapChunkSize);
            if (count > 0)
            {
                return count;
            }
        }

        return Math.Max(entries.Count, 1);
    }

    private static ulong HashEntryName(string name)
    {
        uint eax = 0;
        uint ecx = 0;
        uint edx = 0;
        uint ebx = 0;
        uint esi = 0;
        uint edi = 0;
        ebx = edi = esi = (uint)name.Length + 0xDEADBEEF;
        var i = 0;

        for (i = 0; i + 12 < name.Length; i += 12)
        {
            edi = ((uint)(name[i + 7] << 24) | (uint)(name[i + 6] << 16) | (uint)(name[i + 5] << 8) | name[i + 4]) + edi;
            esi = ((uint)(name[i + 11] << 24) | (uint)(name[i + 10] << 16) | (uint)(name[i + 9] << 8) | name[i + 8]) + esi;
            edx = ((uint)(name[i + 3] << 24) | (uint)(name[i + 2] << 16) | (uint)(name[i + 1] << 8) | name[i]) - esi;

            edx = (edx + ebx) ^ (esi >> 28) ^ (esi << 4);
            esi += edi;
            edi = (edi - edx) ^ (edx >> 26) ^ (edx << 6);
            edx += esi;
            esi = (esi - edi) ^ (edi >> 24) ^ (edi << 8);
            edi += edx;
            ebx = (edx - esi) ^ (esi >> 16) ^ (esi << 16);
            esi += edi;
            edi = (edi - ebx) ^ (ebx >> 13) ^ (ebx << 19);
            ebx += esi;
            esi = (esi - edi) ^ (edi >> 28) ^ (edi << 4);
            edi += ebx;
        }

        if (name.Length - i > 0)
        {
            switch (name.Length - i)
            {
                case 12:
                    esi += (uint)name[i + 11] << 24;
                    goto case 11;
                case 11:
                    esi += (uint)name[i + 10] << 16;
                    goto case 10;
                case 10:
                    esi += (uint)name[i + 9] << 8;
                    goto case 9;
                case 9:
                    esi += name[i + 8];
                    goto case 8;
                case 8:
                    edi += (uint)name[i + 7] << 24;
                    goto case 7;
                case 7:
                    edi += (uint)name[i + 6] << 16;
                    goto case 6;
                case 6:
                    edi += (uint)name[i + 5] << 8;
                    goto case 5;
                case 5:
                    edi += name[i + 4];
                    goto case 4;
                case 4:
                    ebx += (uint)name[i + 3] << 24;
                    goto case 3;
                case 3:
                    ebx += (uint)name[i + 2] << 16;
                    goto case 2;
                case 2:
                    ebx += (uint)name[i + 1] << 8;
                    goto case 1;
                case 1:
                    ebx += name[i];
                    break;
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

    private sealed record UopEntryData(int Index, byte[] Data, ulong Hash);

    private static void WriteMultiEntry(Stream mulStream, byte[] data)
    {
        if (data.Length < 8)
        {
            return;
        }

        var span = data.AsSpan();
        var count = BitConverter.ToUInt32(span.Slice(4, 4));
        var offset = 8;

        using var writer = new BinaryWriter(mulStream, System.Text.Encoding.UTF8, leaveOpen: true);
        for (var i = 0; i < count; i++)
        {
            if (offset + 14 > span.Length)
            {
                break;
            }

            var itemId = BitConverter.ToUInt16(span.Slice(offset, 2));
            var x = BitConverter.ToInt16(span.Slice(offset + 2, 2));
            var y = BitConverter.ToInt16(span.Slice(offset + 4, 2));
            var z = BitConverter.ToInt16(span.Slice(offset + 6, 2));
            var flagValue = BitConverter.ToUInt16(span.Slice(offset + 8, 2));
            var clilocCount = BitConverter.ToUInt32(span.Slice(offset + 10, 4));
            var skip = (int)Math.Min(clilocCount, int.MaxValue) * 4;

            offset += 14 + skip;

            writer.Write(itemId);
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
            writer.Write(flagValue != 0 ? 0 : 1);
            writer.Write(0);
        }
    }

    private const ulong HousingBinHash = 0x126D1E99DDEDEE0A;
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

public sealed record UopOutputSegment(string Path, long Length);

public enum LegacyUopType
{
    Art,
    Gump,
    Map,
    Sound,
    MultiCollection
}
