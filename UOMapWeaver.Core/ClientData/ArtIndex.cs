using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace UOMapWeaver.Core.ClientData;

public sealed class ArtIndex : IDisposable
{
    private readonly int[] _offsets;
    private readonly int[] _lengths;
    private readonly FileStream _artStream;
    private readonly Dictionary<int, string> _hashCache = new();

    private ArtIndex(int[] offsets, int[] lengths, FileStream artStream)
    {
        _offsets = offsets;
        _lengths = lengths;
        _artStream = artStream;
    }

    public int Count => _lengths.Length;

    public static ArtIndex Load(string artIdxPath, string artMulPath)
    {
        if (!File.Exists(artIdxPath))
        {
            throw new FileNotFoundException("artidx.mul not found.", artIdxPath);
        }

        if (!File.Exists(artMulPath))
        {
            throw new FileNotFoundException("art.mul not found.", artMulPath);
        }

        var bytes = File.ReadAllBytes(artIdxPath);
        var entryCount = bytes.Length / 12;
        var offsets = new int[entryCount];
        var lengths = new int[entryCount];

        for (var i = 0; i < entryCount; i++)
        {
            var offset = BitConverter.ToInt32(bytes, i * 12);
            var length = BitConverter.ToInt32(bytes, i * 12 + 4);
            offsets[i] = offset;
            lengths[i] = length;
        }

        var stream = new FileStream(artMulPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new ArtIndex(offsets, lengths, stream);
    }

    public bool HasEntry(int index)
    {
        if (index < 0 || index >= _lengths.Length)
        {
            return false;
        }

        return _offsets[index] >= 0 && _lengths[index] > 0;
    }

    public string? GetEntryHashHex(int index)
    {
        if (!HasEntry(index))
        {
            return null;
        }

        if (_hashCache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var data = ReadEntryData(index);
        if (data is null)
        {
            return null;
        }

        var hash = SHA256.HashData(data);
        var hex = Convert.ToHexString(hash);
        _hashCache[index] = hex;
        return hex;
    }

    private byte[]? ReadEntryData(int index)
    {
        if (!HasEntry(index))
        {
            return null;
        }

        var offset = _offsets[index];
        var length = _lengths[index];
        if (offset < 0 || length <= 0)
        {
            return null;
        }

        var buffer = new byte[length];
        _artStream.Seek(offset, SeekOrigin.Begin);
        var read = _artStream.Read(buffer, 0, length);
        if (read != length)
        {
            return null;
        }

        return buffer;
    }

    public void Dispose()
    {
        _artStream.Dispose();
    }
}
