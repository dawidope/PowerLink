using PowerLink.Core.Scanning;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class FileScannerTests
{
    [Fact]
    public async Task Scan_EnumeratesAllFilesRecursively()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("root.txt", "a");
        temp.CreateFile("sub1/a.txt", "b");
        temp.CreateFile("sub1/sub2/c.txt", "c");

        var scanner = new FileScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path });

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Scan_MinSizeFilter_ExcludesSmallFiles()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("small.txt", new byte[10]);
        temp.CreateFile("big.txt", new byte[1000]);

        var scanner = new FileScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path }, minSizeBytes: 100);

        Assert.Single(result);
        Assert.EndsWith("big.txt", result[0].FullPath);
    }

    [Fact]
    public async Task Scan_EmptyDirectory_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        var scanner = new FileScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path });
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_CapturesFileIndexAndHardLinkCount()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt", "hello");

        var scanner = new FileScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path });

        Assert.Single(result);
        var record = result[0];
        Assert.NotEqual(0ul, record.FileIndex);
        Assert.NotEqual(0u, record.VolumeSerialNumber);
        Assert.Equal(1u, record.HardLinkCount);
        Assert.Equal(5, record.SizeBytes);
    }

    [Fact]
    public async Task Scan_MissingRoot_LogsAndContinues()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt", "x");
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var scanner = new FileScanner();
        var result = await scanner.ScanAsync(new[] { missing, temp.Path });

        Assert.Single(result);
    }
}
