using System.Runtime.InteropServices;
using System.Text;

namespace PowerLink.Core.Native;

public static class DrivePaths
{
    // Mapped network drives and `subst` drives belong to one logon session.
    // An elevated process runs in a different one and doesn't see the user's
    // Z:, so a path like Z:\docs would simply not exist there. This rewrites
    // such paths to a form every session sees: \\server\share\docs for
    // network drives, the real folder for subst drives. Other paths are
    // returned unchanged.
    public static string ToSessionIndependentPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
            return path;

        var drive = path[..2];
        var rest = path[2..];
        // "Z:foo" is relative to Z:'s current directory — leave it alone.
        if (rest.Length > 0 && rest[0] != '\\' && rest[0] != '/')
            return path;

        var root = GetNetworkRoot(drive) ?? GetSubstRoot(drive);
        if (root is null) return path;

        if (rest.Length == 0) return root;
        return root.TrimEnd('\\') + "\\" + rest.TrimStart('\\', '/');
    }

    private static string? GetNetworkRoot(string drive)
    {
        var length = 1024;
        var buffer = new StringBuilder(length);
        return WNetGetConnection(drive, buffer, ref length) == 0 ? buffer.ToString() : null;
    }

    private static string? GetSubstRoot(string drive)
    {
        var buffer = new char[1024];
        var written = QueryDosDevice(drive, buffer, buffer.Length);
        if (written == 0) return null;

        // First entry of a double-null-terminated list. Subst targets look
        // like \??\C:\real\folder or \??\UNC\server\share; ordinary volumes
        // map to \Device\HarddiskVolumeN and are left alone.
        var end = Array.IndexOf(buffer, '\0');
        var target = new string(buffer, 0, end >= 0 ? end : (int)written);
        const string prefix = @"\??\";
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var real = target[prefix.Length..];
        if (real.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            real = @"\\" + real[4..];
        return real;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW", SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, [Out] char[] targetPath, int max);
}
