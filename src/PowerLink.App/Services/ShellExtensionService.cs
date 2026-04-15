using System.Diagnostics;
using Microsoft.Win32;

namespace PowerLink.App.Services;

public static class ShellExtensionService
{
    private const string PickKeyName = "PowerLinkPick";
    private const string DropKeyName = "PowerLinkDrop";
    private const string PickLabel = "PowerLink: Pick as link source";
    private const string DropLabel = "PowerLink: Drop as hardlink here";

    // All registrations live under HKCU so no admin elevation is needed.
    private const string ClassesRoot = @"Software\Classes";

    public static bool IsInstalled()
    {
        using var pickAny    = OpenSubKey($@"{ClassesRoot}\*\shell\{PickKeyName}");
        using var pickFolder = OpenSubKey($@"{ClassesRoot}\Folder\shell\{PickKeyName}");
        using var dropFolder = OpenSubKey($@"{ClassesRoot}\Folder\shell\{DropKeyName}");
        using var dropBg     = OpenSubKey($@"{ClassesRoot}\Directory\Background\shell\{DropKeyName}");
        return pickAny is not null && pickFolder is not null
            && dropFolder is not null && dropBg is not null;
    }

    public static void Install(string cliExePath, string iconSourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliExePath);

        var pickCmd = $"\"{cliExePath}\" pick \"%1\"";
        var dropCmdSelected = $"\"{cliExePath}\" drop \"%1\"";
        var dropCmdBackground = $"\"{cliExePath}\" drop \"%V\"";
        var icon = string.IsNullOrWhiteSpace(iconSourcePath) ? null : $"\"{iconSourcePath}\",0";

        // PICK — on any file or any folder
        WriteVerb($@"{ClassesRoot}\*\shell\{PickKeyName}", PickLabel, icon, pickCmd);
        WriteVerb($@"{ClassesRoot}\Folder\shell\{PickKeyName}", PickLabel, icon, pickCmd);

        // DROP — on a folder (hardlink lands inside it) or in a folder background
        WriteVerb($@"{ClassesRoot}\Folder\shell\{DropKeyName}", DropLabel, icon, dropCmdSelected);
        WriteVerb($@"{ClassesRoot}\Directory\Background\shell\{DropKeyName}", DropLabel, icon, dropCmdBackground);
    }

    public static void Uninstall()
    {
        DeleteSubKeyTree($@"{ClassesRoot}\*\shell\{PickKeyName}");
        DeleteSubKeyTree($@"{ClassesRoot}\Folder\shell\{PickKeyName}");
        DeleteSubKeyTree($@"{ClassesRoot}\Folder\shell\{DropKeyName}");
        DeleteSubKeyTree($@"{ClassesRoot}\Directory\Background\shell\{DropKeyName}");
    }

    public static string DetectCliPath()
    {
        var baseDir = AppContext.BaseDirectory;

        // Production layout — Cli sits next to App.
        var sameFolder = Path.Combine(baseDir, "PowerLink.Cli.exe");
        if (File.Exists(sameFolder)) return sameFolder;

        // Development layout — walk up to find a sibling PowerLink.Cli/bin
        // and glob for the exe under whatever TFM it was built for. Levels
        // cover both `bin/<plat>/<cfg>/<tfm>` and `bin/<cfg>/<tfm>`.
        for (var up = 3; up <= 5; up++)
        {
            var segments = new string[up + 1];
            segments[0] = baseDir;
            for (var i = 1; i <= up; i++) segments[i] = "..";
            var ancestor = Path.GetFullPath(Path.Combine(segments));

            var cliBin = Path.Combine(ancestor, "PowerLink.Cli", "bin");
            if (!Directory.Exists(cliBin)) continue;

            var match = Directory.EnumerateFiles(cliBin, "PowerLink.Cli.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (match is not null) return match;
        }

        // Fall back to expected production location even if it doesn't exist yet.
        return sameFolder;
    }

    public static string DetectIconPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var ico = Path.Combine(baseDir, "Assets", "Icon.ico");
        if (File.Exists(ico)) return ico;
        // Fall back: the App exe carries the embedded icon at index 0.
        return Path.Combine(baseDir, "PowerLink.App.exe");
    }

    public static void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { /* explorer auto-restarts itself */ }
            }
        }
        catch
        {
            // Best effort. Windows respawns explorer.exe automatically.
        }
    }

    private static void WriteVerb(string keyPath, string menuText, string? icon, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"Failed to create registry key: {keyPath}");
        key.SetValue(string.Empty, menuText, RegistryValueKind.String);
        if (icon is not null)
            key.SetValue("Icon", icon, RegistryValueKind.String);

        using var cmdKey = key.CreateSubKey("command", writable: true)
            ?? throw new InvalidOperationException($"Failed to create command sub-key under: {keyPath}");
        cmdKey.SetValue(string.Empty, command, RegistryValueKind.String);
    }

    private static void DeleteSubKeyTree(string keyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Silently ignore — Uninstall is idempotent.
        }
    }

    private static RegistryKey? OpenSubKey(string keyPath)
        => Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
}
