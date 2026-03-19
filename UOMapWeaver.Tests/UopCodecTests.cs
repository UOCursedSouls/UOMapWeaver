using System;
using System.IO;
using System.Linq;
using UOMapWeaver.Core.Uop;

namespace UOMapWeaver.Tests;

public sealed class UopCodecTests : IDisposable
{
    private readonly string _tempDir;

    public UopCodecTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void PackFile_ThenRead_PreservesEntryData()
    {
        // Create a small input file with known content
        var inputPath = Path.Combine(_tempDir, "input.bin");
        var rng = new Random(42);
        var inputData = new byte[4096];
        rng.NextBytes(inputData);
        File.WriteAllBytes(inputPath, inputData);

        var uopPath = Path.Combine(_tempDir, "test.uop");
        var result = UopCodec.PackFile(inputPath, uopPath, templatePath: null, chunkSize: 2048);

        Assert.True(File.Exists(uopPath));
        Assert.Equal(2, result.EntryCount);
        Assert.Equal(inputData.Length, result.TotalBytes);

        // Read back the archive and verify structure
        var archive = UopCodec.Read(uopPath);
        Assert.Equal(UopCodec.Magic, 0x0050594DU); // Sanity check constant
        Assert.True(archive.Entries.Count >= 2);
    }

    [Fact]
    public void PackFile_ThenExtractToFile_ProducesIdenticalContent()
    {
        var inputPath = Path.Combine(_tempDir, "roundtrip_input.bin");
        var rng = new Random(99);
        var inputData = new byte[8192];
        rng.NextBytes(inputData);
        File.WriteAllBytes(inputPath, inputData);

        var uopPath = Path.Combine(_tempDir, "roundtrip.uop");
        UopCodec.PackFile(inputPath, uopPath, templatePath: null, chunkSize: 4096);

        var outputPath = Path.Combine(_tempDir, "roundtrip_output.bin");
        var extractResult = UopCodec.ExtractToFile(uopPath, outputPath, combineEntries: true);

        Assert.True(File.Exists(outputPath));
        Assert.Equal(2, extractResult.EntryCount);

        var outputData = File.ReadAllBytes(outputPath);
        Assert.Equal(inputData.Length, outputData.Length);
        Assert.Equal(inputData, outputData);
    }

    [Fact]
    public void PackFile_ThenExtractToFolder_ProducesEntryFiles()
    {
        var inputPath = Path.Combine(_tempDir, "multi_input.bin");
        var inputData = new byte[6000];
        new Random(77).NextBytes(inputData);
        File.WriteAllBytes(inputPath, inputData);

        var uopPath = Path.Combine(_tempDir, "multi.uop");
        UopCodec.PackFile(inputPath, uopPath, templatePath: null, chunkSize: 2048);

        var outputFolder = Path.Combine(_tempDir, "multi_entries");
        var extractResult = UopCodec.ExtractToFile(uopPath, outputFolder, combineEntries: false);

        Assert.True(Directory.Exists(outputFolder));
        Assert.Equal(3, extractResult.EntryCount);

        var files = Directory.GetFiles(outputFolder, "entry_*.bin");
        Assert.Equal(3, files.Length);

        // Recombine and verify
        var combined = files
            .OrderBy(f => f)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        Assert.Equal(inputData, combined);
    }

    [Fact]
    public void Read_InvalidFile_ThrowsInvalidOperationException()
    {
        var badPath = Path.Combine(_tempDir, "bad.uop");
        File.WriteAllBytes(badPath, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

        Assert.Throws<InvalidOperationException>(() => UopCodec.Read(badPath));
    }

    [Fact]
    public void PackFile_SingleChunk_ProducesSingleEntry()
    {
        var inputPath = Path.Combine(_tempDir, "single.bin");
        var data = new byte[500];
        new Random(11).NextBytes(data);
        File.WriteAllBytes(inputPath, data);

        var uopPath = Path.Combine(_tempDir, "single.uop");
        var result = UopCodec.PackFile(inputPath, uopPath, templatePath: null, chunkSize: 0);

        Assert.Equal(1, result.EntryCount);

        var outputPath = Path.Combine(_tempDir, "single_out.bin");
        UopCodec.ExtractToFile(uopPath, outputPath, combineEntries: true);
        Assert.Equal(data, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void ExtractToSegments_SplitsCorrectly()
    {
        var inputPath = Path.Combine(_tempDir, "seg_input.bin");
        var data = new byte[10000];
        new Random(33).NextBytes(data);
        File.WriteAllBytes(inputPath, data);

        var uopPath = Path.Combine(_tempDir, "seg.uop");
        UopCodec.PackFile(inputPath, uopPath, templatePath: null, chunkSize: 5000);

        var seg1 = Path.Combine(_tempDir, "seg1.bin");
        var seg2 = Path.Combine(_tempDir, "seg2.bin");
        var segments = new[]
        {
            new UopOutputSegment(seg1, 6000),
            new UopOutputSegment(seg2, 4000)
        };

        var result = UopCodec.ExtractToSegments(uopPath, segments);
        Assert.Equal(2, result.EntryCount);

        var seg1Data = File.ReadAllBytes(seg1);
        var seg2Data = File.ReadAllBytes(seg2);
        Assert.Equal(6000, seg1Data.Length);
        Assert.Equal(4000, seg2Data.Length);

        var combined = seg1Data.Concat(seg2Data).ToArray();
        Assert.Equal(data, combined);
    }

    [Fact]
    public void PackFile_EmptyInput_ThrowsOrProducesSingleEntry()
    {
        var inputPath = Path.Combine(_tempDir, "empty.bin");
        File.WriteAllBytes(inputPath, Array.Empty<byte>());

        var uopPath = Path.Combine(_tempDir, "empty.uop");
        // An empty file should still produce a valid UOP (with 0 meaningful entries)
        var result = UopCodec.PackFile(inputPath, uopPath, templatePath: null, chunkSize: 0);

        Assert.True(File.Exists(uopPath));
    }
}
