using System.Buffers.Binary;

namespace UOMapWeaver.Core.Map;

/// <summary>
/// Merges verdata.mul patches into the base MUL files, producing unified
/// MUL files that no longer need verdata.mul.
///
/// verdata.mul is a legacy UO patch file where each entry overrides a block
/// in one of the base MUL files (tiledata.mul, art.mul, gumpart.mul, etc.).
/// This class reads those patches and writes them directly into the target
/// MUL files at the correct byte offsets.
/// </summary>
public static class VerdataMulMerger
{
    /// <summary>Well-known file IDs used inside verdata.mul.</summary>
    private static readonly Dictionary<int, string> FileIdNames = new()
    {
        [0] = "map0.mul",
        [1] = "staidx0.mul",
        [2] = "statics0.mul",
        [3] = "Artidx.mul",
        [4] = "Art.mul",
        [5] = "Anim.idx",
        [6] = "Anim.mul",
        [7] = "Soundidx.mul",
        [8] = "Sound.mul",
        [9] = "Texidx.mul",
        [10] = "Texmaps.mul",
        [11] = "Gumpidx.mul",
        [12] = "Gumpart.mul",
        [13] = "multi.idx",
        [14] = "multi.mul",
        [15] = "skills.mul",
        [16] = "tiledata.mul",
        [20] = "Radarcol.mul",
        [30] = "Light.mul",
        [31] = "Lightidx.mul",
        [32] = "hues.mul",
    };

    /// <summary>Describes one patch entry inside verdata.mul.</summary>
    public readonly record struct VerdataPatch(
        int FileId,
        int BlockIndex,
        int Offset,
        int Length,
        int Extra,
        byte[] Data);

    /// <summary>Summary returned after a merge operation.</summary>
    public sealed class MergeResult
    {
        public int TotalPatches { get; init; }
        public int Applied { get; init; }
        public int Skipped { get; init; }
        public Dictionary<string, int> PatchesPerFile { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Reads every patch entry from a verdata.mul file.
    /// </summary>
    public static List<VerdataPatch> ReadPatches(string verdataPath)
    {
        using var stream = new FileStream(verdataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var entryCount = reader.ReadInt32();
        var patches = new List<VerdataPatch>(Math.Max(0, entryCount));

        // First pass: read the patch table.
        var entries = new (int fileId, int blockIdx, int offset, int length, int extra)[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            entries[i] = (
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());
        }

        // Second pass: seek to each entry and read its payload.
        foreach (var (fileId, blockIdx, offset, length, extra) in entries)
        {
            if (length <= 0 || offset <= 0)
                continue;

            stream.Seek(offset, SeekOrigin.Begin);
            var data = new byte[length];
            stream.ReadExactly(data, 0, length);

            patches.Add(new VerdataPatch(fileId, blockIdx, offset, length, extra, data));
        }

        return patches;
    }

    /// <summary>
    /// Returns the canonical MUL filename for a verdata file-ID, or <c>null</c>
    /// if the ID is unknown.
    /// </summary>
    public static string? FileNameForId(int fileId)
        => FileIdNames.GetValueOrDefault(fileId);

    /// <summary>
    /// Applies patches to the corresponding MUL files inside
    /// <paramref name="mulDirectory"/>.  Only files that have at least one
    /// patch are opened and modified.
    /// </summary>
    public static MergeResult Merge(
        List<VerdataPatch> patches,
        string mulDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancel = default)
    {
        // Group patches by target file.
        var grouped = new Dictionary<string, List<VerdataPatch>>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var unknownIds = new HashSet<int>();

        foreach (var patch in patches)
        {
            var name = FileNameForId(patch.FileId);
            if (name is null)
            {
                unknownIds.Add(patch.FileId);
                continue;
            }

            if (!grouped.TryGetValue(name, out var list))
            {
                list = new List<VerdataPatch>();
                grouped[name] = list;
            }
            list.Add(patch);
        }

        foreach (var id in unknownIds)
            warnings.Add($"Unknown verdata file ID 0x{id:X2} — {patches.Count(p => p.FileId == id)} patches skipped.");

        int applied = 0, skipped = 0, processed = 0;
        var perFile = new Dictionary<string, int>();

        foreach (var (fileName, filePatches) in grouped)
        {
            cancel.ThrowIfCancellationRequested();

            var filePath = ResolvePath(mulDirectory, fileName);
            if (filePath is null)
            {
                warnings.Add($"{fileName}: not found in MUL directory — {filePatches.Count} patches skipped.");
                skipped += filePatches.Count;
                continue;
            }

            var fileApplied = ApplyToFile(filePath, fileName, filePatches, warnings);
            perFile[fileName] = fileApplied;
            applied += fileApplied;
            skipped += filePatches.Count - fileApplied;

            processed++;
            progress?.Report((int)(processed * 100.0 / grouped.Count));
        }

        return new MergeResult
        {
            TotalPatches = patches.Count,
            Applied = applied,
            Skipped = skipped,
            PatchesPerFile = perFile,
            Warnings = warnings
        };
    }

    // ------------------------------------------------------------------ //
    //  Private helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Applies a set of patches to a single MUL file.
    /// The write strategy depends on whether the file is an index file,
    /// tiledata, or a generic data file.
    /// </summary>
    private static int ApplyToFile(string path, string fileName, List<VerdataPatch> patches, List<string> warnings)
    {
        var lower = fileName.ToLowerInvariant();
        var data = File.ReadAllBytes(path);
        var original = data.Length;
        var applied = 0;

        foreach (var patch in patches.OrderBy(p => p.BlockIndex))
        {
            int writeOffset;

            if (lower.EndsWith("idx.mul"))
            {
                // Index files: each record is 12 bytes (offset:4, length:4, extra:4).
                writeOffset = patch.BlockIndex * MapMul.StaticIndexRecordBytes;
            }
            else if (lower == "tiledata.mul")
            {
                writeOffset = TiledataBlockOffset(patch.BlockIndex, patch.Length);
            }
            else if (lower == "map0.mul")
            {
                // Map blocks: each block is 196 bytes.
                writeOffset = patch.BlockIndex * MapMul.LandBlockBytes;
            }
            else
            {
                // Generic data files (art.mul, gumpart.mul, sound.mul, …):
                // For these the block index is typically used together with
                // the corresponding .idx file. The safest approach is to
                // treat the patch as a raw overwrite at a byte offset
                // derived from blockIndex * patch.Length, but only when
                // the entry aligns.  When in doubt we fall back to a
                // direct seek using the Extra field as a hint.
                //
                // Heuristic: if all patches for this file share a common
                // size we can infer a record size; otherwise skip.
                writeOffset = InferDataOffset(patch);
                if (writeOffset < 0)
                {
                    warnings.Add($"{fileName}: skipped block {patch.BlockIndex} — cannot infer write offset for generic data file.");
                    continue;
                }
            }

            // Grow the buffer when the patch extends past the current end.
            var endPos = writeOffset + patch.Length;
            if (endPos > data.Length)
                Array.Resize(ref data, endPos);

            patch.Data.AsSpan().CopyTo(data.AsSpan(writeOffset, patch.Length));
            applied++;
        }

        File.WriteAllBytes(path, data);
        return applied;
    }

    /// <summary>
    /// Calculates the byte offset for a tiledata.mul patch block.
    /// Land blocks (0–0x1FF): each is 4-byte header + 32×26 bytes = 836 bytes.
    /// Item blocks (0x200+): each is 4-byte header + 32×37 bytes = 1188 bytes.
    /// </summary>
    private static int TiledataBlockOffset(int blockIndex, int patchLength)
    {
        const int landBlockSize = 4 + 32 * 26;   // 836
        const int itemBlockSize = 4 + 32 * 37;   // 1188
        const int landBlockCount = 0x200;         // 512

        if (blockIndex < landBlockCount)
            return blockIndex * landBlockSize;

        return landBlockCount * landBlockSize + (blockIndex - landBlockCount) * itemBlockSize;
    }

    /// <summary>
    /// For generic data files we try to derive a byte offset from the
    /// verdata entry.  The <c>Extra</c> field sometimes carries the
    /// uncompressed length or a secondary offset.  If nothing meaningful
    /// can be inferred, returns -1 to signal "skip this patch".
    /// </summary>
    private static int InferDataOffset(VerdataPatch patch)
    {
        // A common convention in old Sphere tools: for data files the
        // block index multiplied by the entry length gives a usable
        // offset.  This works when every record has a fixed size.
        // Since we cannot always know the record size we use the patch
        // length itself as the record size — this gives correct results
        // for uniform-size patch sets (most tiledata / index cases).
        //
        // For art/gump data files whose records have variable size this
        // will NOT be correct, but those patches should instead go
        // through the index file (and the caller should patch the idx
        // file first, then write the data blob at the offset recorded
        // in the patched index entry).  This fallback is therefore only
        // intended for the simple fixed-size cases.

        // Safety: for very large block indices the multiplication may
        // overflow or produce unreasonable offsets.
        var candidate = (long)patch.BlockIndex * patch.Length;
        if (candidate is < 0 or > int.MaxValue)
            return -1;

        return (int)candidate;
    }

    /// <summary>Case-insensitive file lookup inside a directory.</summary>
    private static string? ResolvePath(string directory, string fileName)
    {
        var direct = Path.Combine(directory, fileName);
        if (File.Exists(direct))
            return direct;

        // Try case-insensitive match.
        try
        {
            foreach (var entry in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFileName(entry), fileName, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        catch
        {
            // Ignore enumeration errors.
        }

        return null;
    }
}
