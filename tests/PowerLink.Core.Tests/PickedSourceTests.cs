using PowerLink.Core.State;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class PickedSourceTests
{
    [Fact]
    public void Save_Then_Load_Roundtrips()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.Path, "picked.json");

        var picked = new PickedSource
        {
            Path = @"C:\W\models\some-model.gguf",
            PickedAtUtc = new DateTime(2026, 4, 14, 12, 30, 0, DateTimeKind.Utc),
            IsDirectory = false,
        };
        PickedSourceStore.Save(picked, statePath);

        var loaded = PickedSourceStore.TryLoad(statePath);

        Assert.NotNull(loaded);
        Assert.Equal(picked.Path, loaded!.Path);
        Assert.Equal(picked.PickedAtUtc, loaded.PickedAtUtc);
        Assert.False(loaded.IsDirectory);
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.Path, "nonexistent.json");
        Assert.Null(PickedSourceStore.TryLoad(statePath));
    }

    [Fact]
    public void TryLoad_CorruptedFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.Path, "picked.json");
        File.WriteAllText(statePath, "{ this is not valid json");

        Assert.Null(PickedSourceStore.TryLoad(statePath));
    }

    [Fact]
    public void Save_Overwrites_PreviousValue()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.Path, "picked.json");

        PickedSourceStore.Save(new PickedSource
        {
            Path = @"C:\old.bin", PickedAtUtc = DateTime.UtcNow, IsDirectory = false,
        }, statePath);

        var newPath = @"C:\new\folder";
        PickedSourceStore.Save(new PickedSource
        {
            Path = newPath, PickedAtUtc = DateTime.UtcNow, IsDirectory = true,
        }, statePath);

        var loaded = PickedSourceStore.TryLoad(statePath);
        Assert.Equal(newPath, loaded!.Path);
        Assert.True(loaded.IsDirectory);
    }

    [Fact]
    public void Clear_RemovesFile()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.Path, "picked.json");

        PickedSourceStore.Save(new PickedSource
        {
            Path = @"C:\x", PickedAtUtc = DateTime.UtcNow, IsDirectory = false,
        }, statePath);

        Assert.True(File.Exists(statePath));
        PickedSourceStore.Clear(statePath);
        Assert.False(File.Exists(statePath));
    }
}
