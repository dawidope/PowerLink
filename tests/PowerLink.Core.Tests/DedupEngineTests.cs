using PowerLink.Core.Dedup;
using PowerLink.Core.Scanning;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class DedupEngineTests
{
    [Fact]
    public async Task Analyze_TwoIdenticalFiles_ProducesOneDuplicateGroup()
    {
        using var temp = new TempDirectory();
        var content = new byte[8192];
        new Random(7).NextBytes(content);
        temp.CreateFile("a/file.bin", content);
        temp.CreateFile("b/file.bin", content);

        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });

        var engine = new DedupEngine();
        var result = await engine.AnalyzeAsync(records);

        Assert.Single(result.Groups);
        Assert.Equal(2, result.Groups[0].Files.Count);
        Assert.Equal(1, result.TotalDuplicates);
        Assert.Equal(content.Length, result.TotalWastedBytes);
    }

    [Fact]
    public async Task Analyze_DifferentSizes_ProducesNoGroups()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.bin", new byte[100]);
        temp.CreateFile("b.bin", new byte[200]);

        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });

        var engine = new DedupEngine();
        var result = await engine.AnalyzeAsync(records);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Analyze_SameSizeDifferentContent_ProducesNoGroups()
    {
        using var temp = new TempDirectory();
        var a = new byte[1024];
        var b = new byte[1024];
        new Random(1).NextBytes(a);
        new Random(2).NextBytes(b);
        temp.CreateFile("a.bin", a);
        temp.CreateFile("b.bin", b);

        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });

        var engine = new DedupEngine();
        var result = await engine.AnalyzeAsync(records);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Analyze_AlreadyHardlinked_TreatedAsSinglePhysicalFile()
    {
        using var temp = new TempDirectory();
        var content = new byte[512];
        new Random(3).NextBytes(content);

        var a = temp.CreateFile("a.bin", content);
        var b = Path.Combine(temp.Path, "b.bin");
        PowerLink.Core.Native.Win32Hardlink.CreateHardLink(b, a);

        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });

        var engine = new DedupEngine();
        var result = await engine.AnalyzeAsync(records);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task CreatePlan_ProducesOneActionPerDuplicate()
    {
        using var temp = new TempDirectory();
        var content = new byte[256];
        new Random(5).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);
        temp.CreateFile("c/f.bin", content);

        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });
        var engine = new DedupEngine();
        var result = await engine.AnalyzeAsync(records);

        var plan = DedupEngine.CreatePlan(result);

        Assert.Equal(2, plan.ActionCount);
        Assert.Equal(content.Length * 2, plan.TotalBytesToRecover);
        Assert.All(plan.Actions, a => Assert.NotEqual(a.DuplicatePath, a.CanonicalPath));
    }
}
