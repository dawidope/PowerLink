using PowerLink.Core.Launch;

namespace PowerLink.Core.Tests;

public class PathInputTests
{
    [Theory]
    [InlineData(@"C:\Data", @"C:\Data")]
    [InlineData(@"  C:\Data  ", @"C:\Data")]
    [InlineData("\"C:\\Data\"", @"C:\Data")]              // Explorer "Copy as path"
    [InlineData(" \"C:\\With Space\\f.bin\" ", @"C:\With Space\f.bin")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\a\..\b", @"C:\b")]
    [InlineData(@"C:/Data/sub", @"C:\Data\sub")]
    [InlineData(@"\\server\share\dir", @"\\server\share\dir")]
    [InlineData(@"\\?\C:\Data", @"\\?\C:\Data")]
    public void TryNormalize_AbsolutePaths_AreNormalized(string input, string expected)
    {
        Assert.Equal(expected, PathInput.TryNormalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("Data")]                // relative
    [InlineData(@"Data\sub")]           // relative
    [InlineData(@"C:Data")]             // drive-relative
    [InlineData(@"\Data")]              // root-relative (current drive)
    [InlineData(@"C:")]                 // drive-relative
    public void TryNormalize_EmptyOrRelative_ReturnsNull(string? input)
    {
        Assert.Null(PathInput.TryNormalize(input));
    }
}
