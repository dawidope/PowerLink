using PowerLink.Core.Native;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class Win32JunctionTests
{
    [Fact]
    public void Create_MakesDirectoryAReparsePoint()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var link = Path.Combine(temp.Path, "link");

        Win32Junction.Create(link, target);

        Assert.True(Directory.Exists(link));
        var attrs = File.GetAttributes(link);
        Assert.True(attrs.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(attrs.HasFlag(FileAttributes.Directory));
    }

    [Fact]
    public void Create_LinkNavigatesToTargetContent()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        File.WriteAllText(Path.Combine(target, "hello.txt"), "inside target");
        var link = Path.Combine(temp.Path, "link");

        Win32Junction.Create(link, target);

        var viaLink = File.ReadAllText(Path.Combine(link, "hello.txt"));
        Assert.Equal("inside target", viaLink);
    }

    [Fact]
    public void Create_OnMissingTarget_ByDefaultThrows()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "nonexistent");
        var link = Path.Combine(temp.Path, "link");

        Assert.Throws<DirectoryNotFoundException>(
            () => Win32Junction.Create(link, missing));
    }

    [Fact]
    public void Create_OnMissingTarget_WithAllowMissing_Succeeds()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "nonexistent");
        var link = Path.Combine(temp.Path, "link");

        Win32Junction.Create(link, missing, allowMissingTarget: true);

        Assert.True(Directory.Exists(link));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void Create_OnExistingNonEmptyDirectory_Throws()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var occupied = temp.CreateSubDirectory("occupied");
        File.WriteAllText(Path.Combine(occupied, "blocker.txt"), "x");

        Assert.Throws<IOException>(
            () => Win32Junction.Create(occupied, target));
    }

    [Fact]
    public void Create_OnExistingFile_Throws()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var file = temp.CreateFile("file.txt", "x");

        Assert.Throws<IOException>(
            () => Win32Junction.Create(file, target));
    }

    [Fact]
    public void Read_ReturnsTargetAndNotMissing()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, target);

        var info = Win32Junction.Read(link);

        Assert.NotNull(info);
        Assert.Equal(
            Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
            info!.TargetPath.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
        Assert.False(info.IsTargetMissing);
    }

    [Fact]
    public void Read_TargetDeletedAfterCreation_ReturnsIsTargetMissing()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, target);

        Directory.Delete(target);

        var info = Win32Junction.Read(link);

        Assert.NotNull(info);
        Assert.True(info!.IsTargetMissing);
    }

    [Fact]
    public void Read_OnRegularDirectory_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var dir = temp.CreateSubDirectory("plain");

        var info = Win32Junction.Read(dir);

        Assert.Null(info);
    }

    [Fact]
    public void Read_OnRegularFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("plain.txt", "x");

        var info = Win32Junction.Read(file);

        Assert.Null(info);
    }

    [Fact]
    public void IsJunction_TrueForJunction_FalseOtherwise()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, target);

        var plainDir = temp.CreateSubDirectory("plain");
        var plainFile = temp.CreateFile("plain.txt", "x");

        Assert.True(Win32Junction.IsJunction(link));
        Assert.False(Win32Junction.IsJunction(plainDir));
        Assert.False(Win32Junction.IsJunction(plainFile));
    }

    [Fact]
    public void Delete_RemovesJunction_PreservesTarget()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var targetFile = Path.Combine(target, "keep.txt");
        File.WriteAllText(targetFile, "must survive");
        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, target);

        Win32Junction.Delete(link);

        Assert.False(Directory.Exists(link));
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(targetFile));
        Assert.Equal("must survive", File.ReadAllText(targetFile));
    }

    [Fact]
    public void Delete_WithPopulatedTarget_PreservesAllContent()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var nested = Path.Combine(target, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(target, "a.txt"), "a");
        File.WriteAllText(Path.Combine(nested, "b.txt"), "b");

        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, target);

        Win32Junction.Delete(link);

        Assert.False(Directory.Exists(link));
        Assert.Equal("a", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(nested, "b.txt")));
    }

    [Fact]
    public void Delete_OnNonJunctionDirectory_Throws()
    {
        using var temp = new TempDirectory();
        var plain = temp.CreateSubDirectory("plain");

        Assert.Throws<IOException>(() => Win32Junction.Delete(plain));
    }

    [Fact]
    public void Repair_ChangesTargetToNewPath()
    {
        using var temp = new TempDirectory();
        var targetA = temp.CreateSubDirectory("targetA");
        var targetB = temp.CreateSubDirectory("targetB");
        File.WriteAllText(Path.Combine(targetA, "marker.txt"), "A");
        File.WriteAllText(Path.Combine(targetB, "marker.txt"), "B");

        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, targetA);

        Win32Junction.Repair(link, targetB);

        var info = Win32Junction.Read(link);
        Assert.NotNull(info);
        Assert.Equal(
            Path.GetFullPath(targetB).TrimEnd(Path.DirectorySeparatorChar),
            info!.TargetPath.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
        Assert.Equal("B", File.ReadAllText(Path.Combine(link, "marker.txt")));
    }

    [Fact]
    public void Repair_TargetStillAJunction_NotAFreshDirectory()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateSubDirectory("a");
        var b = temp.CreateSubDirectory("b");
        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, a);

        Win32Junction.Repair(link, b);

        Assert.True(Win32Junction.IsJunction(link));
    }

    [Fact]
    public void Create_UncTarget_Throws()
    {
        using var temp = new TempDirectory();
        var link = Path.Combine(temp.Path, "link");

        Assert.Throws<ArgumentException>(
            () => Win32Junction.Create(link, @"\\some-server\some-share"));
    }

    [Fact]
    public void Create_LongPath_Works()
    {
        using var temp = new TempDirectory();
        var deepSegments = string.Join(
            Path.DirectorySeparatorChar,
            Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 30)));
        var target = Path.Combine(temp.Path, "target", deepSegments);
        Directory.CreateDirectory(target);
        var linkParent = Path.Combine(temp.Path, "links", deepSegments);
        Directory.CreateDirectory(linkParent);
        var link = Path.Combine(linkParent, "link");

        Assert.True(target.Length > 260 || link.Length > 260,
            "Test setup did not produce a long path; adjust segments.");

        Win32Junction.Create(link, target);

        Assert.True(Win32Junction.IsJunction(link));
        var info = Win32Junction.Read(link);
        Assert.NotNull(info);
        Assert.Equal(
            Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
            info!.TargetPath.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
    }

    [Fact]
    public void Create_NormalizesTargetPath()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var link = Path.Combine(temp.Path, "link");

        var denormalized = Path.Combine(temp.Path, "target", ".", "..", "target");
        Win32Junction.Create(link, denormalized);

        var info = Win32Junction.Read(link);
        Assert.NotNull(info);
        Assert.Equal(
            Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
            info!.TargetPath.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
    }
}
