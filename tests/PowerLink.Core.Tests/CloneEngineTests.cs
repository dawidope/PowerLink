using PowerLink.Core.Clone;
using PowerLink.Core.Native;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class CloneEngineTests
{
    [Fact]
    public async Task Clone_CreatesMatchingStructureWithHardlinks()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubDirectory("source");
        var dest = Path.Combine(temp.Path, "dest");

        File.WriteAllText(Path.Combine(source, "a.txt"), "AAA");
        var sub = Path.Combine(source, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "b.txt"), "BBB");

        var engine = new CloneEngine();
        var result = await engine.CloneAsync(source, dest);

        Assert.False(result.DryRun);
        Assert.Equal(2, result.FilesLinked);
        Assert.Equal(0, result.FilesFailed);

        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "b.txt")));

        var infoSrc = Win32Hardlink.GetFileInformation(Path.Combine(source, "a.txt"));
        var infoDst = Win32Hardlink.GetFileInformation(Path.Combine(dest, "a.txt"));
        Assert.Equal(infoSrc.FileIndex, infoDst.FileIndex);
    }

    [Fact]
    public async Task Clone_DryRun_MakesNoChanges()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubDirectory("source");
        var dest = Path.Combine(temp.Path, "dest");
        File.WriteAllText(Path.Combine(source, "a.txt"), "x");

        var engine = new CloneEngine();
        var result = await engine.CloneAsync(source, dest, dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.FilesLinked);
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public async Task Clone_DestInsideSource_Throws()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubDirectory("source");
        var dest = Path.Combine(source, "inside");

        var engine = new CloneEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.CloneAsync(source, dest));
    }

    [Fact]
    public async Task Clone_MissingSource_Throws()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "nope");
        var dest = Path.Combine(temp.Path, "dest");

        var engine = new CloneEngine();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => engine.CloneAsync(missing, dest));
    }

    [Fact]
    public async Task Clone_DestAlreadyExists_NestsUnderSourceName()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubDirectory("mydata");
        File.WriteAllText(Path.Combine(source, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(source, "b.txt"), "world");

        // Dest already exists — engine should treat it as the PARENT and
        // clone into `dest/mydata/` (cp -r / xcopy default).
        var dest = temp.CreateSubDirectory("parent");

        var engine = new CloneEngine();
        var result = await engine.CloneAsync(source, dest);

        var nested = Path.Combine(dest, "mydata");
        Assert.Equal(nested, result.EffectiveDestPath);
        Assert.True(File.Exists(Path.Combine(nested, "a.txt")));
        Assert.True(File.Exists(Path.Combine(nested, "b.txt")));
        Assert.Equal(2, result.FilesLinked);
    }
}
