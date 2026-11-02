using System.Runtime.InteropServices;
using PowerLink.Core.Native;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

public class DrivePathsTests
{
    [Theory]
    [InlineData(@"\\server\share\dir")]
    [InlineData(@"relative\path")]
    [InlineData(@"C:relative")]
    [InlineData("")]
    [InlineData("1:\\x")]
    public void NonDriveOrRelativePaths_AreUnchanged(string path)
    {
        Assert.Equal(path, DrivePaths.ToSessionIndependentPath(path));
    }

    [Fact]
    public void LocalVolumePath_IsUnchanged()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)!; // e.g. C:\
        var path = Path.Combine(system, "Windows");

        Assert.Equal(path, DrivePaths.ToSessionIndependentPath(path));
    }

    // subst drives live in the caller's logon session, same as mapped network
    // drives, so they exercise the same "not visible when elevated" problem
    // without needing a network share.
    [Trait("Category", "Integration")]
    [Theory]
    [InlineData("", "")]
    [InlineData(@"\", @"\")]
    [InlineData(@"\sub\file.txt", @"\sub\file.txt")]
    public void SubstDrive_IsRewrittenToRealFolder(string suffix, string expectedSuffix)
    {
        using var temp = new TempDirectory();
        using var subst = SubstDrive.Create(temp.Path);

        var result = DrivePaths.ToSessionIndependentPath(subst.Drive + suffix);

        Assert.Equal(temp.Path + expectedSuffix, result);
    }

    private sealed class SubstDrive : IDisposable
    {
        private const int DDD_REMOVE_DEFINITION = 0x2;
        private const int DDD_EXACT_MATCH_ON_REMOVE = 0x4;

        private readonly string _target;
        public string Drive { get; }

        private SubstDrive(string drive, string target)
        {
            Drive = drive;
            _target = target;
        }

        public static SubstDrive Create(string target)
        {
            var used = Environment.GetLogicalDrives()
                .Select(d => char.ToUpperInvariant(d[0]))
                .ToHashSet();
            var letter = "ZYXWVUTSRQPONMLKJIHG".FirstOrDefault(c => !used.Contains(c));
            if (letter == default)
                throw new InvalidOperationException("No free drive letter for the subst test.");

            var drive = letter + ":";
            if (!DefineDosDevice(0, drive, target))
                throw new InvalidOperationException($"DefineDosDevice failed: {Marshal.GetLastWin32Error()}");
            return new SubstDrive(drive, target);
        }

        public void Dispose() =>
            DefineDosDevice(DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE, Drive, _target);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DefineDosDeviceW")]
        private static extern bool DefineDosDevice(int flags, string deviceName, string? targetPath);
    }
}
