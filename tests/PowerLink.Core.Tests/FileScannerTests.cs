using System.Security.AccessControl;
using System.Security.Principal;
using PowerLink.Core.Native;
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

    [Fact]
    public async Task Scan_AccessDeniedSubfolder_IsCountedAndSkipped()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("visible.txt", "a");
        temp.CreateFile("locked/hidden.txt", "b");

        using (DenyListing.On(System.IO.Path.Combine(temp.Path, "locked")))
        {
            var scanner = new FileScanner();
            var result = await scanner.ScanAsync(new[] { temp.Path });

            Assert.Single(result);
            Assert.EndsWith("visible.txt", result[0].FullPath);
            Assert.Equal(1, scanner.InaccessibleDirectoryCount);
        }
    }

    [Fact]
    public async Task Scan_AccessDeniedRoot_CountsOneAndReturnsEmpty()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("root/a.txt", "a");
        var root = System.IO.Path.Combine(temp.Path, "root");

        using (DenyListing.On(root))
        {
            var scanner = new FileScanner();
            var result = await scanner.ScanAsync(new[] { root });

            Assert.Empty(result);
            Assert.Equal(1, scanner.InaccessibleDirectoryCount);
        }
    }

    [Fact]
    public async Task Scan_MultipleRoots_SumsInaccessibleCounts()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("r1/locked/a.txt", "a");
        temp.CreateFile("r2/locked/b.txt", "b");
        temp.CreateFile("r3/ok.txt", "c");

        using (DenyListing.On(System.IO.Path.Combine(temp.Path, "r1", "locked")))
        using (DenyListing.On(System.IO.Path.Combine(temp.Path, "r2", "locked")))
        {
            var scanner = new FileScanner();
            var result = await scanner.ScanAsync(new[]
            {
                System.IO.Path.Combine(temp.Path, "r1"),
                System.IO.Path.Combine(temp.Path, "r2"),
                System.IO.Path.Combine(temp.Path, "r3"),
            });

            Assert.Single(result);
            Assert.Equal(2, scanner.InaccessibleDirectoryCount);
        }
    }

    [Fact]
    public async Task Scan_SecondScan_ResetsInaccessibleCount()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("locked/a.txt", "a");
        var scanner = new FileScanner();

        using (DenyListing.On(System.IO.Path.Combine(temp.Path, "locked")))
        {
            await scanner.ScanAsync(new[] { temp.Path });
            Assert.Equal(1, scanner.InaccessibleDirectoryCount);
        }

        var result = await scanner.ScanAsync(new[] { temp.Path });

        Assert.Single(result);
        Assert.Equal(0, scanner.InaccessibleDirectoryCount);
    }

    [Fact]
    public async Task Scan_JunctionToDeniedFolder_IsNotFollowedNorCounted()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("scan/ok.txt", "a");
        temp.CreateFile("elsewhere/secret.txt", "b");
        var elsewhere = System.IO.Path.Combine(temp.Path, "elsewhere");
        Win32Junction.Create(System.IO.Path.Combine(temp.Path, "scan", "link"), elsewhere);

        using (DenyListing.On(elsewhere))
        {
            var scanner = new FileScanner();
            var result = await scanner.ScanAsync(new[] { System.IO.Path.Combine(temp.Path, "scan") });

            Assert.Single(result);
            Assert.Equal(0, scanner.InaccessibleDirectoryCount);
        }
    }

    [Fact]
    public async Task Scan_HiddenAndSystemFiles_AreIncluded()
    {
        using var temp = new TempDirectory();
        var hidden = temp.CreateFile("hidden.txt", "a");
        var system = temp.CreateFile("system.txt", "b");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.SetAttributes(system, FileAttributes.System);
        try
        {
            var result = await new FileScanner().ScanAsync(new[] { temp.Path });
            Assert.Equal(2, result.Count);
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
            File.SetAttributes(system, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Scan_CancelledToken_Throws()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt", "a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FileScanner().ScanAsync(new[] { temp.Path }, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Scan_ReadableTree_ReportsNoInaccessibleDirectories()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("sub/a.txt", "a");

        var scanner = new FileScanner();
        await scanner.ScanAsync(new[] { temp.Path });

        Assert.Equal(0, scanner.InaccessibleDirectoryCount);
    }

    // Adds a Deny-ListDirectory ACE for the current user and removes it on
    // dispose (so TempDirectory can clean up). The deny applies even when the
    // tests run elevated — admins only bypass it with SeBackupPrivilege on.
    private sealed class DenyListing : IDisposable
    {
        private readonly DirectoryInfo _dir;
        private readonly FileSystemAccessRule _rule;

        private DenyListing(DirectoryInfo dir, FileSystemAccessRule rule)
        {
            _dir = dir;
            _rule = rule;
        }

        public static DenyListing On(string path)
        {
            var dir = new DirectoryInfo(path);
            var rule = new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory,
                AccessControlType.Deny);
            var acl = dir.GetAccessControl();
            acl.AddAccessRule(rule);
            dir.SetAccessControl(acl);
            return new DenyListing(dir, rule);
        }

        public void Dispose()
        {
            var acl = _dir.GetAccessControl();
            acl.RemoveAccessRule(_rule);
            _dir.SetAccessControl(acl);
        }
    }
}
