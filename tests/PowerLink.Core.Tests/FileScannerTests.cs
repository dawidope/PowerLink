using System.Security.AccessControl;
using System.Security.Principal;
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
        var locked = new DirectoryInfo(System.IO.Path.Combine(temp.Path, "locked"));

        // A Deny ACE for the current user blocks listing even when the test
        // runs elevated (admins only bypass it with SeBackupPrivilege enabled).
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        var acl = locked.GetAccessControl();
        acl.AddAccessRule(deny);
        locked.SetAccessControl(acl);
        try
        {
            var scanner = new FileScanner();
            var result = await scanner.ScanAsync(new[] { temp.Path });

            Assert.Single(result);
            Assert.EndsWith("visible.txt", result[0].FullPath);
            Assert.Equal(1, scanner.InaccessibleDirectoryCount);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            locked.SetAccessControl(acl);
        }
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
}
