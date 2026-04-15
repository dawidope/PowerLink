using System.Diagnostics;
using Microsoft.Win32;

namespace PowerLink.App.Services;

public enum ShellVerbTarget { Cli, App }

public enum ShellVerbKind { ContextMenu, OverlayHandler }

public sealed record ShellVerbKey(string RelativeKeyPath, string CommandArgs);

public sealed record ShellVerb
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string TargetsText { get; init; }
    public required ShellVerbTarget Executable { get; init; }
    public required IReadOnlyList<ShellVerbKey> Keys { get; init; }
    public ShellVerbKind Kind { get; init; } = ShellVerbKind.ContextMenu;
    public bool RequiresElevation { get; init; }
}

public static class ShellExtensionService
{
    // Context-menu registrations live under HKCU so no admin elevation is needed.
    // Overlay-handler registrations live under HKLM (Windows reads overlays
    // only from HKLM) — those go through PowerLink.Cli with UAC elevation.
    private const string ClassesRoot = @"Software\Classes";

    // Mirrors PowerLink.Cli.OverlayInstaller — keep in sync.
    private const string OverlayClsid = "{8E62D9DE-27D3-4C1E-8A55-CADF97D3EB20}";
    private const string OverlayRegName = " PowerLinkHardlink";
    private const string OverlayRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";

    public static IReadOnlyList<ShellVerb> AllVerbs { get; } = new[]
    {
        new ShellVerb
        {
            Id = "PowerLinkPick",
            Label = "PowerLink: Pick as link source",
            Description = "Remember this path as the source for a later 'Drop as hardlink here'. Both must be on the same NTFS volume.",
            TargetsText = "Files, folders",
            Executable = ShellVerbTarget.Cli,
            Keys = new[]
            {
                new ShellVerbKey(@"*\shell\PowerLinkPick", "pick \"%1\""),
                new ShellVerbKey(@"Folder\shell\PowerLinkPick", "pick \"%1\""),
            },
        },
        new ShellVerb
        {
            Id = "PowerLinkDrop",
            Label = "PowerLink: Drop as hardlink here",
            Description = "Make a hardlink (file) or a tree of hardlinks (folder) from the picked source. Shares data — not a copy, not a symlink.",
            TargetsText = "Folders, folder background",
            Executable = ShellVerbTarget.Cli,
            Keys = new[]
            {
                new ShellVerbKey(@"Folder\shell\PowerLinkDrop", "drop \"%1\""),
                new ShellVerbKey(@"Directory\Background\shell\PowerLinkDrop", "drop \"%V\""),
            },
        },
        new ShellVerb
        {
            Id = "PowerLinkShowLinks",
            Label = "PowerLink: Show hardlinks",
            Description = "Show every path on this volume that is a hardlink to this file's data. Quick dialog, no scan.",
            TargetsText = "Files",
            Executable = ShellVerbTarget.Cli,
            Keys = new[]
            {
                new ShellVerbKey(@"*\shell\PowerLinkShowLinks", "show-links \"%1\""),
            },
        },
        new ShellVerb
        {
            Id = "PowerLinkInspect",
            Label = "PowerLink: Inspect for hardlinks",
            Description = "Open PowerLink's Inspector with this folder — finds files inside that are already hardlinked and shows every sibling path.",
            TargetsText = "Folders, folder background",
            Executable = ShellVerbTarget.App,
            Keys = new[]
            {
                new ShellVerbKey(@"Folder\shell\PowerLinkInspect", "--inspect \"%1\""),
                new ShellVerbKey(@"Directory\Background\shell\PowerLinkInspect", "--inspect \"%V\""),
            },
        },
        new ShellVerb
        {
            Id = "PowerLinkDedup",
            Label = "PowerLink: Deduplicate folder",
            Description = "Open PowerLink's Deduplicate page with this folder added. Finds identical files and replaces duplicates with hardlinks to reclaim disk space.",
            TargetsText = "Folders, folder background",
            Executable = ShellVerbTarget.App,
            Keys = new[]
            {
                new ShellVerbKey(@"Folder\shell\PowerLinkDedup", "--dedup \"%1\""),
                new ShellVerbKey(@"Directory\Background\shell\PowerLinkDedup", "--dedup \"%V\""),
            },
        },
        new ShellVerb
        {
            Id = "PowerLinkClone",
            Label = "PowerLink: Clone folder (hardlinks)",
            Description = "Open PowerLink's Clone page with this folder as source. Mirrors the tree as hardlinks — not a copy, no extra disk used, same volume only.",
            TargetsText = "Folders",
            Executable = ShellVerbTarget.App,
            Keys = new[]
            {
                new ShellVerbKey(@"Folder\shell\PowerLinkClone", "--clone \"%1\""),
            },
        },
    };

    public static IReadOnlyList<ShellVerb> OverlayVerbs { get; } = new[]
    {
        new ShellVerb
        {
            Id = "PowerLinkHardlinkOverlay",
            Label = "Hardlink overlay icon",
            Description = "Show a small badge on the icon of any file that's a hardlink (has 2+ names on disk). Quick visual check across every Explorer window. Uses 1 of 15 Windows overlay slots.",
            TargetsText = "All files in Explorer",
            Executable = ShellVerbTarget.Cli,
            Keys = Array.Empty<ShellVerbKey>(),
            Kind = ShellVerbKind.OverlayHandler,
            RequiresElevation = true,
        },
    };

    public static bool IsInstalled(ShellVerb verb)
    {
        if (verb.Kind == ShellVerbKind.OverlayHandler)
            return IsOverlayInstalled();

        foreach (var key in verb.Keys)
        {
            using var k = OpenSubKey($@"{ClassesRoot}\{key.RelativeKeyPath}");
            if (k is null) return false;
        }
        return true;
    }

    public static bool IsAnyInstalled() => AllVerbs.Any(IsInstalled);

    public static bool IsOverlayInstalled()
    {
        using var cls = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Classes\CLSID\{OverlayClsid}\InprocServer32");
        using var overlay = Registry.LocalMachine.OpenSubKey(
            $@"{OverlayRoot}\{OverlayRegName}");
        return cls is not null && overlay is not null;
    }

    public static int CountOverlayHandlersOnSystem()
    {
        using var root = Registry.LocalMachine.OpenSubKey(OverlayRoot);
        return root?.GetSubKeyNames().Length ?? 0;
    }

    public static void Install(ShellVerb verb, string cliPath, string appPath, string iconPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appPath);

        var exe = verb.Executable == ShellVerbTarget.Cli ? cliPath : appPath;
        var icon = string.IsNullOrWhiteSpace(iconPath) ? null : $"\"{iconPath}\",0";

        foreach (var key in verb.Keys)
        {
            var command = $"\"{exe}\" {key.CommandArgs}";
            WriteVerb($@"{ClassesRoot}\{key.RelativeKeyPath}", verb.Label, icon, command);
        }
    }

    public static void Uninstall(ShellVerb verb)
    {
        foreach (var key in verb.Keys)
            DeleteSubKeyTree($@"{ClassesRoot}\{key.RelativeKeyPath}");
    }

    public static void UninstallAll()
    {
        foreach (var verb in AllVerbs)
            Uninstall(verb);
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

        return sameFolder;
    }

    public static string DetectAppPath()
    {
        // The running App's own exe is the right target for --dedup / --inspect / --clone.
        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self) && File.Exists(self)) return self;
        return Path.Combine(AppContext.BaseDirectory, "PowerLink.App.exe");
    }

    public static string DetectShellExtDllPath()
    {
        var baseDir = AppContext.BaseDirectory;

        // Production layout — DLL sits next to App.exe (post-build copy).
        var sameFolder = Path.Combine(baseDir, "PowerLink.ShellExt.dll");
        if (File.Exists(sameFolder)) return sameFolder;

        // Development layout — sibling project's bin folder.
        for (var up = 3; up <= 6; up++)
        {
            var segments = new string[up + 1];
            segments[0] = baseDir;
            for (var i = 1; i <= up; i++) segments[i] = "..";
            var ancestor = Path.GetFullPath(Path.Combine(segments));

            var dllBin = Path.Combine(ancestor, "PowerLink.ShellExt", "x64");
            if (!Directory.Exists(dllBin)) continue;

            var match = Directory.EnumerateFiles(dllBin, "PowerLink.ShellExt.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (match is not null) return match;
        }

        return sameFolder;
    }

    public static string DetectIconPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var ico = Path.Combine(baseDir, "Assets", "Icon.ico");
        if (File.Exists(ico)) return ico;
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
            // Silently ignore — uninstall is idempotent.
        }
    }

    private static RegistryKey? OpenSubKey(string keyPath)
        => Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
}
