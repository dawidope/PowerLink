using PowerLink.Core.Dedup;
using PowerLink.Core.Models;
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
        var plan = new DedupPlan { Actions = Array.Empty<DedupAction>() };
        var executor = new DedupExecutor();
        var result = await executor.ExecuteAsync(plan);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.False(result.WasCancelled);
    }

    [Fact]
    public async Task Execute_CancelledAfterFirstAction_ReturnsPartialResult()
    {
        using var temp = new TempDirectory();
        var c1 = new byte[4096]; new Random(21).NextBytes(c1);
        temp.CreateFile("g1/a.bin", c1); temp.CreateFile("g1/b.bin", c1);
        var c2 = new byte[4096]; new Random(22).NextBytes(c2);
        temp.CreateFile("g2/a.bin", c2); temp.CreateFile("g2/b.bin", c2);
        var c3 = new byte[4096]; new Random(23).NextBytes(c3);
        temp.CreateFile("g3/a.bin", c3); temp.CreateFile("g3/b.bin", c3);

        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });
        var engine = new DedupEngine();
        var scanResult = await engine.AnalyzeAsync(records);
        var plan = DedupEngine.CreatePlan(scanResult);
        Assert.Equal(3, plan.ActionCount);

        using var cts = new CancellationTokenSource();
        var progress = new InlineCancellingProgress(cts);

        var executor = new DedupExecutor();
        var result = await executor.ExecuteAsync(plan, cts.Token, progress);

        Assert.True(result.WasCancelled);
        Assert.True(result.SuccessCount >= 1 && result.SuccessCount < plan.ActionCount,
            $"Expected 1..{plan.ActionCount - 1} successes, got {result.SuccessCount}");
        Assert.True(result.BytesRecovered > 0);
    }

    [Fact]
    public async Task Execute_CanonicalMissing_ReportsFailureAndLeavesDuplicateIntact()
    {
        using var temp = new TempDirectory();
        var dupPath = temp.CreateFile("dup.bin", new byte[10]);
        var missingCanonical = Path.Combine(temp.Path, "missing.bin");

        var plan = new DedupPlan
        {
            Actions = new[]
            {
                new DedupAction
                {
                    DuplicatePath = dupPath,
                    CanonicalPath = missingCanonical,
                    SizeBytes = 10,
                    Hash = "abc",
                },
            },
        };

        var executor = new DedupExecutor();
        var result = await executor.ExecuteAsync(plan);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        // Duplicate must survive — failure fires from VerifyCanonical before File.Delete.
        Assert.True(File.Exists(dupPath));
    }

    [Fact]
    public async Task Execute_OneActionFails_ContinuesRemainingActions()
    {
        using var temp = new TempDirectory();
        var good = new byte[1024];
        new Random(31).NextBytes(good);
        var goodCanonical = temp.CreateFile("ok/a.bin", good);
        var goodDuplicate = temp.CreateFile("ok/b.bin", good);

        var plan = new DedupPlan
        {
            Actions = new[]
            {
                // Fails: canonical doesn't exist.
                new DedupAction
                {
                    DuplicatePath = Path.Combine(temp.Path, "nope", "dup.bin"),
                    CanonicalPath = Path.Combine(temp.Path, "nope", "missing.bin"),
                    SizeBytes = 10,
                    Hash = "abc",
                },
                // Succeeds.
                new DedupAction
                {
                    DuplicatePath = goodDuplicate,
                    CanonicalPath = goodCanonical,
                    SizeBytes = good.Length,
                    Hash = "good",
                },
            },
        };

        var executor = new DedupExecutor();
        var result = await executor.ExecuteAsync(plan);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(good.Length, result.BytesRecovered);

        var infoCanonical = Win32Hardlink.GetFileInformation(goodCanonical);
        var infoDuplicate = Win32Hardlink.GetFileInformation(goodDuplicate);
        Assert.Equal(infoCanonical.FileIndex, infoDuplicate.FileIndex);
    }

    private sealed class InlineCancellingProgress : IProgress<ScanProgress>
    {
        private readonly CancellationTokenSource _cts;
        public InlineCancellingProgress(CancellationTokenSource cts) => _cts = cts;
        public void Report(ScanProgress value)
        {
            // Executor calls Report synchronously at the end of each action,
            // so cancelling here fires ThrowIfCancellationRequested on the
            // next iteration — exercising the partial-result path.
            if (value.FilesProcessed >= 1) _cts.Cancel();
        }
    }
}
