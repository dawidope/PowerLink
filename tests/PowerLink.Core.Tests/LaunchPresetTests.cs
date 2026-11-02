using PowerLink.Core.Launch;

namespace PowerLink.Core.Tests;

public class LaunchPresetTests
{
    [Fact]
    public void Parse_NoArgs_ReturnsNull()
    {
        Assert.Null(LaunchPreset.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void Parse_UnknownArgsOnly_ReturnsNull()
    {
        Assert.Null(LaunchPreset.Parse(new[] { "--foo", @"C:\x", "bar" }));
    }

    [Fact]
    public void Parse_SingleShellVerb_ReturnsModeAndPath()
    {
        var preset = LaunchPreset.Parse(new[] { "--inspect", @"C:\Data" });

        Assert.NotNull(preset);
        Assert.Equal(LaunchMode.Inspect, preset.Mode);
        Assert.Equal(new[] { @"C:\Data" }, preset.Paths);
    }

    [Fact]
    public void Parse_RepeatedFlag_CollectsAllPathsInOrder()
    {
        var preset = LaunchPreset.Parse(new[] { "--dedup", @"C:\A", "--dedup", @"D:\B" });

        Assert.NotNull(preset);
        Assert.Equal(LaunchMode.Dedup, preset.Mode);
        Assert.Equal(new[] { @"C:\A", @"D:\B" }, preset.Paths);
    }

    [Fact]
    public void Parse_MixedModes_FirstModeWinsAndOthersAreSkipped()
    {
        var preset = LaunchPreset.Parse(new[] { "--dedup", @"C:\A", "--clone", @"C:\B", "--dedup", @"C:\C" });

        Assert.NotNull(preset);
        Assert.Equal(LaunchMode.Dedup, preset.Mode);
        Assert.Equal(new[] { @"C:\A", @"C:\C" }, preset.Paths);
    }

    [Fact]
    public void Parse_TrailingFlagWithoutPath_IsIgnored()
    {
        var preset = LaunchPreset.Parse(new[] { "--dedup", @"C:\A", "--dedup" });

        Assert.NotNull(preset);
        Assert.Equal(new[] { @"C:\A" }, preset.Paths);
    }

    [Fact]
    public void Parse_FlagAsOnlyArg_ReturnsNull()
    {
        Assert.Null(LaunchPreset.Parse(new[] { "--dedup" }));
    }

    [Fact]
    public void Parse_PathThatLooksLikeAFlag_IsConsumedAsPath()
    {
        // A folder literally named "--clone" must not switch modes.
        var preset = LaunchPreset.Parse(new[] { "--dedup", "--clone", "--dedup", @"C:\B" });

        Assert.NotNull(preset);
        Assert.Equal(LaunchMode.Dedup, preset.Mode);
        Assert.Equal(new[] { "--clone", @"C:\B" }, preset.Paths);
    }

    [Theory]
    [InlineData(LaunchMode.Dedup)]
    [InlineData(LaunchMode.Inspect)]
    [InlineData(LaunchMode.Clone)]
    [InlineData(LaunchMode.Junction)]
    public void ToArguments_RoundTripsThroughParse(LaunchMode mode)
    {
        var original = new LaunchPreset(mode, new[] { @"C:\", @"D:\With Space\", @"\\server\share\dir" });

        var parsed = LaunchPreset.Parse(original.ToArguments().ToList());

        Assert.NotNull(parsed);
        Assert.Equal(original.Mode, parsed.Mode);
        Assert.Equal(original.Paths, parsed.Paths);
    }

    [Fact]
    public void ToArguments_EmptyPaths_ProducesNoArguments()
    {
        Assert.Empty(new LaunchPreset(LaunchMode.Dedup, Array.Empty<string>()).ToArguments());
    }

    [Fact]
    public void FlagAndModeFromFlag_AreInverse()
    {
        foreach (var mode in Enum.GetValues<LaunchMode>())
            Assert.Equal(mode, LaunchPreset.ModeFromFlag(LaunchPreset.Flag(mode)));
    }
}
