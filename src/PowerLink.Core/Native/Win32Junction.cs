using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PowerLink.Core.Native;

public sealed record JunctionInfo(string LinkPath, string TargetPath, bool IsTargetMissing);

public static class Win32Junction
{
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    private const int MaxReparseBufferSize = 16 * 1024; // kernel limit 16 KB
    private const string NtPathPrefix = @"\??\";

    public static void Create(string linkPath, string targetPath, bool allowMissingTarget = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (targetPath.StartsWith(@"\\", StringComparison.Ordinal)
            && !targetPath.StartsWith(NtPathPrefix, StringComparison.Ordinal)
            && !targetPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Junctions cannot target UNC paths. Use a symlink instead.",
                nameof(targetPath));
        }

        var targetFull = Path.GetFullPath(targetPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!allowMissingTarget && !Directory.Exists(targetFull))
        {
            throw new DirectoryNotFoundException(
                $"Junction target does not exist: '{targetFull}'. " +
                $"Pass allowMissingTarget=true to create a dangling junction.");
        }

        var linkFull = Path.GetFullPath(linkPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (File.Exists(linkFull))
        {
            throw new IOException(
                $"Cannot create junction: '{linkFull}' already exists as a file.");
        }
        if (Directory.Exists(linkFull))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(linkFull).Any())
                {
                    throw new IOException(
                        $"Cannot create junction: '{linkFull}' already exists and is not empty.");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException(
                    $"Cannot create junction: '{linkFull}' exists but cannot be inspected.", ex);
            }
        }
        else
        {
            Directory.CreateDirectory(linkFull);
        }

        try
        {
            WriteReparseData(linkFull, targetFull);
        }
        catch
        {
            try { Directory.Delete(linkFull); } catch { }
            throw;
        }
    }

    public static JunctionInfo? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(full);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (!attrs.HasFlag(FileAttributes.ReparsePoint)
            || !attrs.HasFlag(FileAttributes.Directory))
        {
            return null;
        }

        using var handle = OpenReparseHandle(full, forWrite: false);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"CreateFileW failed for '{full}' (Win32 error {err}: {new Win32Exception(err).Message})",
                err);
        }

        var buffer = new byte[MaxReparseBufferSize];
        if (!NativeMethods.DeviceIoControl(
                handle,
                FSCTL_GET_REPARSE_POINT,
                IntPtr.Zero, 0,
                buffer, (uint)buffer.Length,
                out var bytesReturned,
                IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"FSCTL_GET_REPARSE_POINT failed for '{full}' (Win32 error {err}: {new Win32Exception(err).Message})",
                err);
        }

        var tag = BitConverter.ToUInt32(buffer, 0);
        if (tag != IO_REPARSE_TAG_MOUNT_POINT)
        {
            // Not a junction (could be a symlink, WSL, deduplicated file, etc.)
            return null;
        }

        // Layout after header (8 bytes = tag + dataLen + reserved):
        //  [8]  SubstituteNameOffset (USHORT)
        //  [10] SubstituteNameLength (USHORT)
        //  [12] PrintNameOffset (USHORT)
        //  [14] PrintNameLength (USHORT)
        //  [16] PathBuffer
        var subNameOffset = BitConverter.ToUInt16(buffer, 8);
        var subNameLen = BitConverter.ToUInt16(buffer, 10);

        var substitute = Encoding.Unicode.GetString(buffer, 16 + subNameOffset, subNameLen);
        var target = StripNtPrefix(substitute);

        return new JunctionInfo(full, target, !Directory.Exists(target));
    }

    public static bool IsJunction(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(path) is not null;
    }

    public static void Delete(string linkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);

        var full = Path.GetFullPath(linkPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!IsJunction(full))
        {
            throw new IOException(
                $"Not a junction: '{full}'. Refusing to delete to avoid touching a real directory.");
        }

        // RemoveDirectoryW on a junction removes the reparse point directory
        // entry without recursing into the target — this is the documented,
        // safe way to unlink a junction.
        if (!NativeMethods.RemoveDirectoryW(Win32Hardlink.ToExtendedPath(full)))
        {
            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"RemoveDirectoryW failed for junction '{full}' (Win32 error {err}: {new Win32Exception(err).Message})",
                err);
        }
    }

    public static void Repair(string linkPath, string newTargetPath, bool allowMissingTarget = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newTargetPath);

        var linkFull = Path.GetFullPath(linkPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!IsJunction(linkFull))
        {
            throw new IOException($"Cannot repair: '{linkFull}' is not a junction.");
        }

        if (newTargetPath.StartsWith(@"\\", StringComparison.Ordinal)
            && !newTargetPath.StartsWith(NtPathPrefix, StringComparison.Ordinal)
            && !newTargetPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Junctions cannot target UNC paths.",
                nameof(newTargetPath));
        }

        var newTargetFull = Path.GetFullPath(newTargetPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!allowMissingTarget && !Directory.Exists(newTargetFull))
        {
            throw new DirectoryNotFoundException(
                $"New junction target does not exist: '{newTargetFull}'.");
        }

        // FSCTL_SET_REPARSE_POINT overwrites in place when the tag matches —
        // no need to delete first. This is atomic from the directory entry's
        // perspective: readers see either the old target or the new, never
        // a "no junction" intermediate state.
        WriteReparseData(linkFull, newTargetFull);
    }

    private static void WriteReparseData(string linkFull, string targetFull)
    {
        var substituteName = NtPathPrefix + targetFull;
        var printName = targetFull;

        var subBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);

        // Layout after ReparseTag (4) + ReparseDataLength (2) + Reserved (2):
        //   SubstituteNameOffset (2)
        //   SubstituteNameLength (2)
        //   PrintNameOffset (2)
        //   PrintNameLength (2)
        //   PathBuffer [substitute\0 printName\0]
        var pathBufferLen = subBytes.Length + 2 + printBytes.Length + 2;
        var reparseDataLen = 8 + pathBufferLen;
        var total = 8 + reparseDataLen;

        var buffer = new byte[total];
        BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)reparseDataLen).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6); // Reserved

        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                              // SubstituteNameOffset
        BitConverter.GetBytes((ushort)subBytes.Length).CopyTo(buffer, 10);               // SubstituteNameLength (excl null)
        BitConverter.GetBytes((ushort)(subBytes.Length + 2)).CopyTo(buffer, 12);         // PrintNameOffset (after sub + null)
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);             // PrintNameLength (excl null)

        subBytes.CopyTo(buffer, 16);
        // null terminator for sub at 16 + subBytes.Length
        printBytes.CopyTo(buffer, 16 + subBytes.Length + 2);
        // null terminator for print at end

        using var handle = OpenReparseHandle(linkFull, forWrite: true);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"CreateFileW for write failed: '{linkFull}' (Win32 error {err}: {new Win32Exception(err).Message})",
                err);
        }

        if (!NativeMethods.DeviceIoControl(
                handle,
                FSCTL_SET_REPARSE_POINT,
                buffer, (uint)total,
                null, 0,
                out _,
                IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"FSCTL_SET_REPARSE_POINT failed: '{linkFull}' (Win32 error {err}: {new Win32Exception(err).Message})",
                err);
        }
    }

    private static SafeFileHandle OpenReparseHandle(string path, bool forWrite)
    {
        var access = forWrite ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ;
        return NativeMethods.CreateFileW(
            Win32Hardlink.ToExtendedPath(path),
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
    }

    private static string StripNtPrefix(string substituteName)
    {
        if (substituteName.StartsWith(NtPathPrefix, StringComparison.Ordinal))
            return substituteName[NtPathPrefix.Length..];
        return substituteName;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[]? lpInBuffer,
            uint nInBufferSize,
            byte[]? lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            byte[]? lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RemoveDirectoryW(string lpPathName);
    }
}
