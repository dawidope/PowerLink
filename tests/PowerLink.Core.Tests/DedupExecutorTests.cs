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
                    // Snapshot values are irrelevant — verify aborts at the
                    // canonical File.Exists check before consulting them.
                    CanonicalSnapshot = default,
                    DuplicateSnapshot = default,
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

        // Build a real plan over the good pair so its action carries real
        // snapshots + hash. Splice in a hand-built failure action up front.
        var goodPlan = await BuildPlanAsync(temp);
        Assert.Single(goodPlan.Actions);

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
                    CanonicalSnapshot = default,
                    DuplicateSnapshot = default,
                },
                goodPlan.Actions[0],
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

    // Engine assigns canonical = lowest fileIndex inside the group, so the
    // physical (a/f.bin, b/f.bin) → (canonical, duplicate) mapping is
    // non-deterministic. Tests below mutate via plan.Actions[0].* paths
    // rather than the temp-file variables to stay role-correct.

    [Fact]
    public async Task Execute_DuplicateContentChanged_AbortsAndPreservesDuplicate()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(71).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var plan = await BuildPlanAsync(temp);
        Assert.Single(plan.Actions);
        var dupPath = plan.Actions[0].DuplicatePath;

        // Modify dup in place — same size, different content. NTFS bumps
        // mtime naturally on write, so the executor should re-hash and reject.
        var mutated = new byte[content.Length];
        new Random(72).NextBytes(mutated);
        File.WriteAllBytes(dupPath, mutated);

        var result = await new DedupExecutor().ExecuteAsync(plan);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Contains("Duplicate content changed", result.Failures[0].Reason);
        Assert.Equal(mutated, File.ReadAllBytes(dupPath));
    }

    [Fact]
    public async Task Execute_DuplicateReplacedSameContent_AbortsBecauseFileIndexChanged()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(73).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var plan = await BuildPlanAsync(temp);
        var dupPath = plan.Actions[0].DuplicatePath;

        // Atomically replace dup: delete + recreate yields a new MFT record
        // (different fileIndex). Conservative verify rejects this even though
        // the content matches — the file we scanned no longer exists at this
        // path and we shouldn't dedupe a file we never saw.
        File.Delete(dupPath);
        File.WriteAllBytes(dupPath, content);

        var result = await new DedupExecutor().ExecuteAsync(plan);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Contains("Duplicate file replaced", result.Failures[0].Reason);
        Assert.Equal(content, File.ReadAllBytes(dupPath));
    }

    [Fact]
    public async Task Execute_CanonicalReplaced_AbortsAndPreservesAll()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(81).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var plan = await BuildPlanAsync(temp);
        var canonPath = plan.Actions[0].CanonicalPath;
        var dupPath = plan.Actions[0].DuplicatePath;

        // Replace canonical: same path + same bytes but new fileIndex.
        File.Delete(canonPath);
        File.WriteAllBytes(canonPath, content);

        var result = await new DedupExecutor().ExecuteAsync(plan);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Contains("Canonical file replaced", result.Failures[0].Reason);
        Assert.True(File.Exists(dupPath));
        Assert.Equal(content, File.ReadAllBytes(dupPath));
    }

    [Fact]
    public async Task Execute_DuplicateAlreadyHardlinkedToCanonical_ReportsAsAlreadyLinked()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(91).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var plan = await BuildPlanAsync(temp);
        var canonPath = plan.Actions[0].CanonicalPath;
        var dupPath = plan.Actions[0].DuplicatePath;

        // External tool (or a previous dedup run) hardlinks dup to canonical
        // between scan and apply. Verify must detect the shared physical file
        // and treat the action as a no-op rather than delete-and-relink.
        // The dup path's fileIndex now equals canonical's — but also the
        // canonical's *snapshot* fileIndex still matches (canonical itself
        // was untouched), so the canonical-replaced check passes too.
        File.Delete(dupPath);
        Win32Hardlink.CreateHardLink(dupPath, canonPath);

        var result = await new DedupExecutor().ExecuteAsync(plan);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(1, result.AlreadyLinkedCount);
        Assert.Equal(0, result.BytesRecovered);
    }

    [Fact]
    public async Task Execute_MtimeChangedButContentSame_RehashesAndProceeds()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(101).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var plan = await BuildPlanAsync(temp);
        var canonPath = plan.Actions[0].CanonicalPath;
        var dupPath = plan.Actions[0].DuplicatePath;

        // Touch mtime forward. Identity (size, fileIndex, volSerial) is intact,
        // so verify falls through to a re-hash. Re-hash matches recorded value
        // → action proceeds.
        File.SetLastWriteTimeUtc(dupPath, DateTime.UtcNow.AddHours(1));

        var result = await new DedupExecutor().ExecuteAsync(plan);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);

        var infoCanon = Win32Hardlink.GetFileInformation(canonPath);
        var infoDup = Win32Hardlink.GetFileInformation(dupPath);
        Assert.Equal(infoCanon.FileIndex, infoDup.FileIndex);
    }

    [Fact]
    public async Task Execute_AlwaysVerifyContent_DetectsContentSwapWithRestoredMtime()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(111).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });
        var scanResult = await new DedupEngine().AnalyzeAsync(records);
        var plan = DedupEngine.CreatePlan(scanResult);
        Assert.Single(plan.Actions);
        var dupPath = plan.Actions[0].DuplicatePath;
        var dupRecord = records.First(r => r.FullPath == dupPath);

        // Stealth mutation: rewrite dup with different bytes (same size), then
        // restore the original mtime. Without AlwaysVerifyContent the tiered
        // verify trusts mtime and would proceed with a destructive delete.
        // With AlwaysVerifyContent on, the re-hash catches the swap.
        var mutated = new byte[content.Length];
        new Random(112).NextBytes(mutated);
        File.WriteAllBytes(dupPath, mutated);
        File.SetLastWriteTimeUtc(dupPath, dupRecord.LastWriteTimeUtc);

        var result = await new DedupExecutor().ExecuteAsync(
            plan, options: new DedupExecutorOptions { AlwaysVerifyContent = true });

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Contains("Duplicate content changed", result.Failures[0].Reason);
        Assert.Equal(mutated, File.ReadAllBytes(dupPath));
    }

    // Group A — P0 #1: delete-then-hardlink atomicity.
    // Currently DedupExecutor calls File.Delete(dup) then CreateHardLink(dup,
    // canonical). If CreateHardLink throws, the duplicate is gone forever and
    // there is no rollback. Tests below inject a throwing hardlink delegate to
    // simulate that failure mode deterministically.

    [Fact]
    public async Task Execute_HardLinkFailsAfterDelete_DuplicateRestoredWithOriginalContent()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(201).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var plan = await BuildPlanAsync(temp);
        Assert.Single(plan.Actions);
        var dupPath = plan.Actions[0].DuplicatePath;

        var executor = new DedupExecutor(
            createHardLink: (newLink, existing) =>
                throw new IOException("simulated CreateHardLink failure"));
        var result = await executor.ExecuteAsync(plan);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.True(File.Exists(dupPath),
            $"Duplicate at '{dupPath}' should have been restored after the hardlink failed.");
        Assert.Equal(content, File.ReadAllBytes(dupPath));
    }

    [Fact]
    public async Task Execute_HardLinkFails_NoStageFileLeftBehind()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(202).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var plan = await BuildPlanAsync(temp);
        var dupPath = plan.Actions[0].DuplicatePath;
        var dupDir = Path.GetDirectoryName(dupPath)!;

        var executor = new DedupExecutor(
            createHardLink: (_, _) => throw new IOException("simulated failure"));
        await executor.ExecuteAsync(plan);

        var staleStageFiles = Directory
            .EnumerateFiles(dupDir, "*.pl-stage-*")
            .ToArray();
        Assert.Empty(staleStageFiles);
    }

    [Fact]
    public async Task Execute_HardLinkFailsThenSecondActionSucceeds_ProcessesBothCorrectly()
    {
        using var temp = new TempDirectory();
        var c1 = new byte[2048]; new Random(203).NextBytes(c1);
        temp.CreateFile("g1/a.bin", c1);
        temp.CreateFile("g1/b.bin", c1);
        var c2 = new byte[2048]; new Random(204).NextBytes(c2);
        temp.CreateFile("g2/a.bin", c2);
        temp.CreateFile("g2/b.bin", c2);

        var plan = await BuildPlanAsync(temp);
        Assert.Equal(2, plan.Actions.Count);
        var firstDup = plan.Actions[0].DuplicatePath;
        var secondDup = plan.Actions[1].DuplicatePath;

        var executor = new DedupExecutor(
            createHardLink: (newLink, existing) =>
            {
                if (string.Equals(newLink, firstDup, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("simulated failure on first action only");
                Win32Hardlink.CreateHardLink(newLink, existing);
            });
        var result = await executor.ExecuteAsync(plan);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.True(File.Exists(firstDup),
            "First duplicate should have been restored after its hardlink failed.");
        Assert.Equal(c1, File.ReadAllBytes(firstDup));

        var infoSecondCanon = Win32Hardlink.GetFileInformation(plan.Actions[1].CanonicalPath);
        var infoSecondDup = Win32Hardlink.GetFileInformation(secondDup);
        Assert.Equal(infoSecondCanon.FileIndex, infoSecondDup.FileIndex);
    }

    [Fact]
    public async Task Execute_HardLinkSucceeds_NoStageFileLeftBehind()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(205).NextBytes(content);
        temp.CreateFile("a/f.bin", content);
        temp.CreateFile("b/f.bin", content);

        var plan = await BuildPlanAsync(temp);
        var dupDir = Path.GetDirectoryName(plan.Actions[0].DuplicatePath)!;

        var result = await new DedupExecutor().ExecuteAsync(plan);
        Assert.Equal(1, result.SuccessCount);

        var staleStageFiles = Directory
            .EnumerateFiles(dupDir, "*.pl-stage-*")
            .ToArray();
        Assert.Empty(staleStageFiles);
    }

    // Group B — long-path E2E. Win32Hardlink.ToExtendedPath prepends \\?\ for
    // paths >= 260 chars. These tests exercise it end-to-end to confirm the C#
    // layer is path-length-safe (P0 #2 truncation is ShellExt-only).

    [Fact]
    public void Win32Hardlink_CreateHardLink_PathOver260Chars_Succeeds()
    {
        using var temp = new TempDirectory();
        var deep = BuildDeepPath(temp.Path, minTotalLength: 320);
        Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        var content = new byte[1024];
        new Random(301).NextBytes(content);
        File.WriteAllBytes(deep, content);
        Assert.True(deep.Length >= 320, $"Test setup: expected path >= 320 chars, got {deep.Length}.");

        var linkPath = deep + ".link";
        Win32Hardlink.CreateHardLink(linkPath, deep);

        var info = Win32Hardlink.GetFileInformation(deep);
        Assert.Equal(2u, info.HardLinkCount);
        Assert.Equal(content.Length, info.SizeBytes);
    }

    [Fact]
    public void Win32Hardlink_GetFileInformation_PathOver260Chars_ReturnsCorrectSize()
    {
        using var temp = new TempDirectory();
        var deep = BuildDeepPath(temp.Path, minTotalLength: 300);
        Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        var content = new byte[2048];
        new Random(302).NextBytes(content);
        File.WriteAllBytes(deep, content);

        var info = Win32Hardlink.GetFileInformation(deep);
        Assert.Equal(content.Length, info.SizeBytes);
        Assert.Equal(1u, info.HardLinkCount);
    }

    private static string BuildDeepPath(string root, int minTotalLength)
    {
        var sb = new System.Text.StringBuilder(root);
        // ~30-char segment names — predictable, no spaces, no Windows reserved chars.
        const string segment = "deep_segment_aaaaaaaaaaaaaaaaa";
        const string fileTail = "\\file.bin";
        while (sb.Length + fileTail.Length < minTotalLength)
            sb.Append(Path.DirectorySeparatorChar).Append(segment);
        sb.Append(fileTail);
        return sb.ToString();
    }

    private static async Task<DedupPlan> BuildPlanAsync(TempDirectory temp)
    {
        var scanner = new FileScanner();
        var records = await scanner.ScanAsync(new[] { temp.Path });
        var scanResult = await new DedupEngine().AnalyzeAsync(records);
        return DedupEngine.CreatePlan(scanResult);
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
