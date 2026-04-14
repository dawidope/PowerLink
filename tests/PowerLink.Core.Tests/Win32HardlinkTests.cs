using PowerLink.Core.Native;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class Win32HardlinkTests
{
    [Fact]
    public void CreateHardLink_CreatesLinkWithSameContent()
    {
        using var temp = new TempDirectory();
        var original = temp.CreateFile("original.bin", new byte[] { 1, 2, 3, 4, 5 });
        var link = Path.Combine(temp.Path, "link.bin");

        Win32Hardlink.CreateHardLink(link, original);

        Assert.True(File.Exists(link));
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(link));
    }

    [Fact]
    public void CreateHardLink_IncrementsHardLinkCount()
    {
        using var temp = new TempDirectory();
        var original = temp.CreateFile("file.bin", new byte[] { 42 });

        var before = Win32Hardlink.GetFileInformation(original);
        Assert.Equal(1u, before.HardLinkCount);

        var link = Path.Combine(temp.Path, "link.bin");
        Win32Hardlink.CreateHardLink(link, original);

        var after = Win32Hardlink.GetFileInformation(original);
        Assert.Equal(2u, after.HardLinkCount);
    }

    [Fact]
    public void CreateHardLink_ProducesSameFileIndex()
    {
        using var temp = new TempDirectory();
        var original = temp.CreateFile("file.bin", new byte[] { 7, 8, 9 });
        var link = Path.Combine(temp.Path, "link.bin");

        Win32Hardlink.CreateHardLink(link, original);

        var originalInfo = Win32Hardlink.GetFileInformation(original);
        var linkInfo = Win32Hardlink.GetFileInformation(link);

        Assert.Equal(originalInfo.FileIndex, linkInfo.FileIndex);
        Assert.Equal(originalInfo.VolumeSerialNumber, linkInfo.VolumeSerialNumber);
    }

    [Fact]
    public void DeletingOneLink_DoesNotAffectOther()
    {
        using var temp = new TempDirectory();
        var original = temp.CreateFile("a.bin", "payload");
        var link = Path.Combine(temp.Path, "b.bin");
        Win32Hardlink.CreateHardLink(link, original);

        File.Delete(original);

        Assert.False(File.Exists(original));
        Assert.True(File.Exists(link));
        Assert.Equal("payload", File.ReadAllText(link));
    }

    [Fact]
    public void CreateHardLink_OnNonExistentSource_Throws()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "does-not-exist.bin");
        var link = Path.Combine(temp.Path, "link.bin");

        Assert.Throws<IOException>(() => Win32Hardlink.CreateHardLink(link, missing));
    }

    [Fact]
    public void AreSameVolume_ReturnsTrueForSameRoot()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateFile("a.txt", "x");
        var b = temp.CreateSubDirectory("sub");

        Assert.True(Win32Hardlink.AreSameVolume(a, b));
    }

    [Fact]
    public void ToExtendedPath_ShortPath_ReturnsUnmodified()
    {
        var input = @"C:\Users\test\file.txt";
        var result = Win32Hardlink.ToExtendedPath(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ToExtendedPath_AlreadyExtended_ReturnsUnmodified()
    {
        var input = @"\\?\C:\Users\test\file.txt";
        var result = Win32Hardlink.ToExtendedPath(input);
        Assert.Equal(input, result);
    }
}
