using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PowerLink.Core.Native;

public readonly record struct FileInformation(
    uint HardLinkCount,
    ulong FileIndex,
    uint VolumeSerialNumber,
    long SizeBytes,
    DateTime LastWriteTimeUtc);

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
        var mtimeTicks = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
        var mtime = DateTime.FromFileTimeUtc(mtimeTicks);
        return new FileInformation(info.NumberOfLinks, fileIndex, info.VolumeSerialNumber, size, mtime);
    }

    public static bool AreSameVolume(string path1, string path2)
    {
        var root1 = Path.GetPathRoot(Path.GetFullPath(path1));
        var root2 = Path.GetPathRoot(Path.GetFullPath(path2));
        return string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase);
    }

    // Returns every full path on the same volume that shares this file's
    // data. For a non-hardlinked file the list contains only `path` itself.
    // Uses FindFirstFileNameW / FindNextFileNameW, which return paths
    // relative to the volume root (e.g. "\Users\x\file.txt"). We prepend
    // the drive letter from `path` to produce absolute paths.
    public static IReadOnlyList<string> EnumerateHardLinks(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var root = (Path.GetPathRoot(full) ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var results = new List<string>();
        var handle = FindFirstLink(full, out var firstLink);
        try
        {
            results.Add(root + firstLink);
            while (TryFindNextLink(handle, out var next))
                results.Add(root + next);
        }
        finally
        {
            NativeMethods.FindClose(handle);
        }
        return results;
    }

    private static IntPtr FindFirstLink(string path, out string firstLink)
    {
        var pathExt = ToExtendedPath(path);
        var size = (uint)MaxTraditionalPath;
        var buffer = new StringBuilder((int)size);
        var handle = NativeMethods.FindFirstFileNameW(pathExt, 0, ref size, buffer);

        if (handle == InvalidHandle)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorMoreData)
            {
                buffer = new StringBuilder((int)size);
                handle = NativeMethods.FindFirstFileNameW(pathExt, 0, ref size, buffer);
                if (handle == InvalidHandle)
                    err = Marshal.GetLastWin32Error();
            }
            if (handle == InvalidHandle)
                throw new IOException(
                    $"FindFirstFileNameW failed for '{path}' (Win32 error {err}: {new Win32Exception(err).Message})",
                    err);
        }
        firstLink = buffer.ToString();
        return handle;
    }

    private static bool TryFindNextLink(IntPtr handle, out string next)
    {
        var size = (uint)MaxTraditionalPath;
        var buffer = new StringBuilder((int)size);
        if (NativeMethods.FindNextFileNameW(handle, ref size, buffer))
        {
            next = buffer.ToString();
            return true;
        }
        var err = Marshal.GetLastWin32Error();
        if (err == ErrorMoreData)
        {
            buffer = new StringBuilder((int)size);
            if (NativeMethods.FindNextFileNameW(handle, ref size, buffer))
            {
                next = buffer.ToString();
                return true;
            }
            err = Marshal.GetLastWin32Error();
        }
        if (err == ErrorHandleEof)
        {
            next = string.Empty;
            return false;
        }
        throw new IOException(
            $"FindNextFileNameW failed (Win32 error {err}: {new Win32Exception(err).Message})",
            err);
    }

    private static readonly IntPtr InvalidHandle = new(-1);
    private const int ErrorMoreData = 234;
    private const int ErrorHandleEof = 38;

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindFirstFileNameW(
            string lpFileName,
            uint dwFlags,
            ref uint StringLength,
            StringBuilder LinkName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextFileNameW(
            IntPtr hFindStream,
            ref uint StringLength,
            StringBuilder LinkName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr hFindFile);
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
