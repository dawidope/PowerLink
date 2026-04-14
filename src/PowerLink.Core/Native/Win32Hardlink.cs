using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerLink.Core.Native;

public readonly record struct FileInformation(
    uint HardLinkCount,
    ulong FileIndex,
    uint VolumeSerialNumber,
    long SizeBytes);

public static class Win32Hardlink
{
    private const int MaxTraditionalPath = 260;
    private const string ExtendedPrefix = @"\\?\";
    private const string UncExtendedPrefix = @"\\?\UNC\";

    public static void CreateHardLink(string newLinkPath, string existingFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newLinkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(existingFilePath);

        var newExt = ToExtendedPath(newLinkPath);
        var existingExt = ToExtendedPath(existingFilePath);

        if (!NativeMethods.CreateHardLinkW(newExt, existingExt, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"CreateHardLink failed: '{newLinkPath}' -> '{existingFilePath}' (Win32 error {err}: {new Win32Exception(err).Message})",
                err);
        }
    }

    public static FileInformation GetFileInformation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);

        return GetFileInformation(handle);
    }

    public static FileInformation GetFileInformation(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"GetFileInformationByHandle failed (Win32 error {err}: {new Win32Exception(err).Message})",
                err);
        }

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        return new FileInformation(info.NumberOfLinks, fileIndex, info.VolumeSerialNumber, size);
    }

    public static bool AreSameVolume(string path1, string path2)
    {
        var root1 = Path.GetPathRoot(Path.GetFullPath(path1));
        var root2 = Path.GetPathRoot(Path.GetFullPath(path2));
        return string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase);
    }

    public static string ToExtendedPath(string path)
    {
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            return path;

        var full = Path.GetFullPath(path);
        if (full.Length < MaxTraditionalPath)
            return full;

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return UncExtendedPrefix + full[2..];

        return ExtendedPrefix + full;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLinkW(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
