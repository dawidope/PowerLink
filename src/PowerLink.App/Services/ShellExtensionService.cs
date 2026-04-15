using System.Diagnostics;
using Microsoft.Win32;

namespace PowerLink.App.Services;

public enum ShellVerbTarget { Cli, App }

public sealed record ShellVerbKey(string RelativeKeyPath, string CommandArgs);

public sealed record ShellVerb
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string TargetsText { get; init; }
    public required ShellVerbTarget Executable { get; init; }
    public required IReadOnlyList<ShellVerbKey> Keys { get; init; }
}

public static class ShellExtensionService
{
    // All registrations live under HKCU so no admin elevation is needed.
    private const string ClassesRoot = @"Software\Classes";

    public static IReadOnlyList<ShellVerb> AllVerbs { get; } = new[]
    {
        new ShellVerb
        {
            Id = "PowerLinkPick",
            Label = "PowerLink: Pick as link source",
            Description = "Remember this file or folder as the source for a later 'Drop as hardlink here'.",
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
            Description = "Create a hardlink (for files) or a cloned tree (for folders) from the previously picked source.",
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
            Description = "List every path that shares this file's data on disk — quick dialog, no scan.",
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
            Description = "Open PowerLink's Inspector with this folder — finds files inside that are already hardlinked.",
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
            Description = "Open PowerLink's Deduplicate page with this folder added to the scan list.",
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
            Description = "Open PowerLink's Clone page with this folder pre-filled as the source.",
            TargetsText = "Folders",
            Executable = ShellVerbTarget.App,
            Keys = new[]
            {
                new ShellVerbKey(@"Folder\shell\PowerLinkClone", "--clone \"%1\""),
            },
        },
    };

    public static bool IsInstalled(ShellVerb verb)
    {
        foreach (var key in verb.Keys)
        {
            using var k = OpenSubKey($@"{ClassesRoot}\{key.RelativeKeyPath}");
            if (k is null) return false;
        }
        return true;
    }

    public static bool IsAnyInstalled() => AllVerbs.Any(IsInstalled);

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
