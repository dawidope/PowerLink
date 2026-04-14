using PowerLink.Core.Dedup;
using PowerLink.Core.Native;
using PowerLink.Core.Scanning;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class DedupExecutorTests
{
    [Fact]
    public async Task Execute_ReplacesDuplicateWithHardlink()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(11).NextBytes(content);

        var canonical = temp.CreateFile("a/f.bin", content);
        var duplicate = temp.CreateFile("b/f.bin", content);

        var infoCanonicalBefore = Win32Hardlink.GetFileInformation(canonical);
        var infoDuplicateBefore = Win32Hardlink.GetFileInformation(duplicate);
        Assert.NotEqual(infoCanonicalBefore.FileIndex, infoDuplicateBefore.FileIndex);

        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });
        var engine = new DedupEngine();
        var result = await engine.AnalyzeAsync(records);
        var plan = DedupEngine.CreatePlan(result);

        var executor = new DedupExecutor();
        var execResult = await executor.ExecuteAsync(plan);

        Assert.Equal(1, execResult.SuccessCount);
        Assert.Equal(0, execResult.FailureCount);
        Assert.Equal(content.Length, execResult.BytesRecovered);

        Assert.True(File.Exists(canonical));
        Assert.True(File.Exists(duplicate));
        Assert.Equal(content, File.ReadAllBytes(duplicate));

        var infoCanonicalAfter = Win32Hardlink.GetFileInformation(canonical);
        var infoDuplicateAfter = Win32Hardlink.GetFileInformation(duplicate);
        Assert.Equal(infoCanonicalAfter.FileIndex, infoDuplicateAfter.FileIndex);
        Assert.Equal(2u, infoCanonicalAfter.HardLinkCount);
    }

    [Fact]
    public async Task Execute_EmptyPlan_DoesNothing()
    {
        var plan = new Models.DedupPlan { Actions = Array.Empty<Models.DedupAction>() };
        var executor = new DedupExecutor();
        var result = await executor.ExecuteAsync(plan);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
    }
}
