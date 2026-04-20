using PowerLink.Core.Clone;
using PowerLink.Core.Native;
using PowerLink.Core.Scanning;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

// Regression tests for the "don't follow reparse points" invariant. The
// default FileScanner and CloneEngine use
// EnumerationOptions.AttributesToSkip = FileAttributes.ReparsePoint, which
// prevents infinite recursion on junction cycles AND prevents dedup/clone
// from silently modifying the target of a junction. These tests lock that
// in — if someone removes that flag, the tests fail loudly.
[Trait("Category", "Integration")]
public class JunctionAwareEnumerationTests
{
    [Fact]
    public async Task Scan_TreeWithSelfReferentialJunction_DoesNotRecurseForever()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("file.bin", new byte[] { 1, 2, 3 });

        // Junction pointing back at its own parent — classic cycle.
        var loop = Path.Combine(temp.Path, "loop");
        Win32Junction.Create(loop, temp.Path);

        var scanner = new FileScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path });

        // Only the real file; nothing walked via the junction.
        Assert.Single(result);
        Assert.EndsWith("file.bin", result[0].FullPath);
    }

    [Fact]
    public async Task Scan_TreeWithJunctionToAnotherFolder_DoesNotFollowJunction()
    {
        using var temp = new TempDirectory();
        var scanRoot = temp.CreateSubDirectory("scan-root");
        var elsewhere = temp.CreateSubDirectory("elsewhere");
        File.WriteAllText(Path.Combine(scanRoot, "mine.bin"), "mine");
        File.WriteAllText(Path.Combine(elsewhere, "theirs.bin"), "theirs");

        Win32Junction.Create(Path.Combine(scanRoot, "link"), elsewhere);

        var scanner = new FileScanner();
        var result = await scanner.ScanAsync(new[] { scanRoot });

        Assert.Single(result);
        Assert.EndsWith("mine.bin", result[0].FullPath);
    }

    [Fact]
    public async Task Clone_SourceContainingJunction_DoesNotHardlinkTargetContent()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubDirectory("source");
        var dest = temp.CreateSubDirectory("dest");
        var elsewhere = temp.CreateSubDirectory("elsewhere");

        File.WriteAllText(Path.Combine(source, "a.bin"), "source-content");
        File.WriteAllText(Path.Combine(elsewhere, "secret.bin"), "secret-content");

        // Junction inside source pointing at elsewhere. If CloneEngine
        // followed it, the clone would have a hardlink to secret.bin — which
        // could later be dedup'd, rewriting the "secret" behind the user's
        // back.
        Win32Junction.Create(Path.Combine(source, "link"), elsewhere);

        var result = await new CloneEngine().CloneAsync(source, dest);

        // Only source/a.bin should be linked in the dest. Not secret.bin.
        Assert.Equal(1, result.FilesLinked);

        // dest/link should NOT exist as either a junction or a regular
        // directory containing elsewhere's content.
        var destLink = Path.Combine(result.EffectiveDestPath, "link");
        var destSecret = Path.Combine(result.EffectiveDestPath, "link", "secret.bin");
        Assert.False(File.Exists(destSecret),
            "Clone should not have followed the junction and replicated its target's contents.");
        // Whether the junction itself is replicated is a future feature. For
        // now the invariant is: target content is NOT copied/linked through.
        if (Directory.Exists(destLink))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(destLink));
        }
    }
}
