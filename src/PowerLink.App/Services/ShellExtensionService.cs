using System.Diagnostics;
using Microsoft.Win32;

namespace PowerLink.App.Services;

public enum ShellVerbTarget { Cli, App }

public enum ShellVerbKind { ContextMenu, OverlayHandler, DropHandler, ModernMenu }

public enum ContextMenuLayout { Flat, Grouped }

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

    // Mirrors CLSID_PowerLinkDropHandler in DropHandler.h.
    private const string DropClsid = "{4BA9E8F3-7D1A-4E6C-9E3B-8F2D7C4A1B59}";
    private const string DropRegName = "PowerLinkDrop";
    private const string DropDescription = "PowerLink Drop Handler";

    // Preferences key for things like the chosen context-menu layout.
    private const string PrefsKey = @"Software\PowerLink";

    // Grouped layout: each Explorer target type gets its own submenu store under
    // HKCU\Software\Classes (mirror of HKCR) so ExtendedSubCommandsKey can point
    // at it. Keeping separate stores per target lets us show only the verbs that
    // actually apply to that context without any dynamic filtering.
    private static readonly (string Target, string Store)[] GroupedStores =
    {
        (@"*",                    "PowerLinkSubmenu.File"),
        (@"Folder",               "PowerLinkSubmenu.Folder"),
        (@"Directory\Background", "PowerLinkSubmenu.Background"),
    };

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

    public static IReadOnlyList<ShellVerb> DropVerbs { get; } = new[]
    {
        new ShellVerb
        {
            Id = "PowerLinkDropHandler",
            Label = "PowerLink drop handler",
            Description = "Right-drag files/folders onto a folder and choose 'Hardlink here' or 'Clone tree here'. One DLL adds both menu items. Per-user HKCU, no admin.",
            TargetsText = "Right-drag onto Folder",
            Executable = ShellVerbTarget.App,
            Keys = Array.Empty<ShellVerbKey>(),
            Kind = ShellVerbKind.DropHandler,
            RequiresElevation = false,
        },
    };

    public static IReadOnlyList<ShellVerb> ModernMenuVerbs { get; } = new[]
    {
        new ShellVerb
        {
            Id = "PowerLinkModernMenu",
            Label = "Windows 11 modern menu (experimental)",
            Description = "Adds a PowerLink submenu to the top section of Win11 file/folder right-click menu — no 'Show more options' needed. Registers a sparse MSIX package; requires Windows Developer Mode.",
            TargetsText = "Right-click any file or folder",
            Executable = ShellVerbTarget.App,
            Keys = Array.Empty<ShellVerbKey>(),
            Kind = ShellVerbKind.ModernMenu,
            RequiresElevation = false,
        },
    };

    public static bool IsInstalled(ShellVerb verb)
    {
        if (verb.Kind == ShellVerbKind.OverlayHandler)
            return IsOverlayInstalled();
        if (verb.Kind == ShellVerbKind.DropHandler)
            return IsDropHandlerInstalled();
        if (verb.Kind == ShellVerbKind.ModernMenu)
            return ModernMenuService.IsInstalled();

        var layout = GetLayout();
        foreach (var key in verb.Keys)
        {
            var path = layout == ContextMenuLayout.Grouped
                ? GroupedLookupPath(key.RelativeKeyPath)
                : $@"{ClassesRoot}\{key.RelativeKeyPath}";
            if (path is null) continue;
            using var k = OpenSubKey(path);
            if (k is null) return false;
        }
        return true;
    }

    public static bool IsAnyInstalled() => AllVerbs.Any(IsInstalled);

    public static ContextMenuLayout GetLayout()
    {
        using var k = Registry.CurrentUser.OpenSubKey(PrefsKey);
        var s = k?.GetValue("ContextMenuLayout") as string;
        return string.Equals(s, nameof(ContextMenuLayout.Grouped), StringComparison.OrdinalIgnoreCase)
            ? ContextMenuLayout.Grouped
            : ContextMenuLayout.Flat;
    }

    public static void SetLayout(ContextMenuLayout layout)
    {
        using var k = Registry.CurrentUser.CreateSubKey(PrefsKey, writable: true)
            ?? throw new InvalidOperationException("Failed to create PowerLink prefs key.");
        k.SetValue("ContextMenuLayout", layout.ToString(), RegistryValueKind.String);
    }

    private static string? ExtractTarget(string relKey)
    {
        var idx = relKey.IndexOf(@"\shell\", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : relKey[..idx];
    }

    private static string ExtractVerbId(string relKey)
    {
        var idx = relKey.LastIndexOf('\\');
        return idx < 0 ? relKey : relKey[(idx + 1)..];
    }

    private static string? GroupedLookupPath(string relKey)
    {
        var target = ExtractTarget(relKey);
        if (target is null) return null;
        var store = GroupedStores.FirstOrDefault(
            g => g.Target.Equals(target, StringComparison.OrdinalIgnoreCase)).Store;
        if (string.IsNullOrEmpty(store)) return null;
        return $@"{ClassesRoot}\{store}\shell\{ExtractVerbId(relKey)}";
    }

    public static bool IsOverlayInstalled()
    {
        using var cls = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Classes\CLSID\{OverlayClsid}\InprocServer32");
        using var overlay = Registry.LocalMachine.OpenSubKey(
            $@"{OverlayRoot}\{OverlayRegName}");
        if (cls is null || overlay is null) return false;

        // Guard against stale registrations: if the DLL the registry points at
        // no longer exists (user cleaned bin/, moved the build, reinstalled
        // somewhere else), surface that as "not installed" so Settings and
        // Apply reflect reality instead of a phantom registration.
        return cls.GetValue(string.Empty) is string dll && File.Exists(dll);
    }

    public static int CountOverlayHandlersOnSystem()
    {
        using var root = Registry.LocalMachine.OpenSubKey(OverlayRoot);
        return root?.GetSubKeyNames().Length ?? 0;
    }

    /// <summary>
    /// Returns the raw key names under HKLM's ShellIconOverlayIdentifiers —
    /// leading-space tricks (OneDrive, ours) are preserved so the user sees
    /// exactly what's competing for the 15 slots Windows loads.
    /// </summary>
    public static IReadOnlyList<string> ListOverlayHandlers()
    {
        using var root = Registry.LocalMachine.OpenSubKey(OverlayRoot);
        if (root is null) return Array.Empty<string>();
        return root.GetSubKeyNames()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsDropHandlerInstalled()
    {
        using var cls = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesRoot}\CLSID\{DropClsid}\InprocServer32");
        using var drop = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesRoot}\Directory\shellex\DragDropHandlers\{DropRegName}");
        if (cls is null || drop is null) return false;

        // Same stale-registration guard as IsOverlayInstalled — if the DLL has
        // disappeared since we registered it, don't claim the handler is live.
        return cls.GetValue(string.Empty) is string dll && File.Exists(dll);
    }

    public static void InstallDropHandler(string dllPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("Shell extension DLL not found.", dllPath);

        using (var clsid = Registry.CurrentUser.CreateSubKey(
            $@"{ClassesRoot}\CLSID\{DropClsid}", writable: true)
            ?? throw new InvalidOperationException("Failed to create CLSID key."))
        {
            clsid.SetValue(string.Empty, DropDescription, RegistryValueKind.String);
            using var inproc = clsid.CreateSubKey("InprocServer32", writable: true)
                ?? throw new InvalidOperationException("Failed to create InprocServer32 key.");
            inproc.SetValue(string.Empty, dllPath, RegistryValueKind.String);
            inproc.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
        }

        using var drop = Registry.CurrentUser.CreateSubKey(
            $@"{ClassesRoot}\Directory\shellex\DragDropHandlers\{DropRegName}", writable: true)
            ?? throw new InvalidOperationException("Failed to create drop handler registration key.");
        drop.SetValue(string.Empty, DropClsid, RegistryValueKind.String);
    }

    public static void UninstallDropHandler()
    {
        DeleteSubKeyTree($@"{ClassesRoot}\Directory\shellex\DragDropHandlers\{DropRegName}");
        DeleteSubKeyTree($@"{ClassesRoot}\CLSID\{DropClsid}");
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

    /// <summary>
    /// Wipes every PowerLink context-menu registration under HKCU — both flat
    /// and grouped layouts, parent entries and submenu stores alike. Called at
    /// the start of an Apply to guarantee the resulting state matches exactly
    /// what the UI asked for, without orphaned entries from a previous layout.
    /// </summary>
    public static void UninstallAll()
    {
        foreach (var verb in AllVerbs)
            Uninstall(verb);

        foreach (var (target, store) in GroupedStores)
        {
            DeleteSubKeyTree($@"{ClassesRoot}\{target}\shell\PowerLink");
            DeleteSubKeyTree($@"{ClassesRoot}\{store}");
        }
    }

    /// <summary>
    /// Single entry point for Apply: tears down any previous PowerLink HKCU
    /// state, persists the layout preference, then writes the verbs the user
    /// selected under the shape that layout dictates.
    /// </summary>
    public static void ApplyContextMenuSelection(
        IEnumerable<(ShellVerb verb, bool install)> selection,
        string cliPath, string appPath, string iconPath,
        ContextMenuLayout layout)
    {
        UninstallAll();
        SetLayout(layout);

        var toInstall = selection.Where(s => s.install).Select(s => s.verb).ToList();
        if (toInstall.Count == 0) return;

        if (layout == ContextMenuLayout.Flat)
        {
            foreach (var verb in toInstall)
                Install(verb, cliPath, appPath, iconPath);
            return;
        }

        var targetsWithVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var verb in toInstall)
        {
            InstallGrouped(verb, cliPath, appPath, iconPath);
            foreach (var key in verb.Keys)
            {
                var target = ExtractTarget(key.RelativeKeyPath);
                if (target is not null) targetsWithVerbs.Add(target);
            }
        }
        WriteGroupedParents(iconPath, targetsWithVerbs);
    }

    private static void InstallGrouped(ShellVerb verb, string cliPath, string appPath, string iconPath)
    {
        var exe = verb.Executable == ShellVerbTarget.Cli ? cliPath : appPath;
        var icon = string.IsNullOrWhiteSpace(iconPath) ? null : $"\"{iconPath}\",0";

        // Inside a cascading submenu the "PowerLink:" prefix is redundant — the
        // parent already says PowerLink. Strip it for cleaner submenu labels.
        var label = verb.Label.StartsWith("PowerLink: ", StringComparison.Ordinal)
            ? verb.Label["PowerLink: ".Length..]
            : verb.Label;

        foreach (var key in verb.Keys)
        {
            var target = ExtractTarget(key.RelativeKeyPath);
            if (target is null) continue;
            var store = GroupedStores.FirstOrDefault(
                g => g.Target.Equals(target, StringComparison.OrdinalIgnoreCase)).Store;
            if (string.IsNullOrEmpty(store)) continue;

            var subKey = $@"{ClassesRoot}\{store}\shell\{ExtractVerbId(key.RelativeKeyPath)}";
            var command = $"\"{exe}\" {key.CommandArgs}";
            WriteVerb(subKey, label, icon, command);
        }
    }

    private static void WriteGroupedParents(string iconPath, HashSet<string> targetsWithVerbs)
    {
        var icon = string.IsNullOrWhiteSpace(iconPath) ? null : $"\"{iconPath}\",0";
        foreach (var (target, store) in GroupedStores)
        {
            var parentPath = $@"{ClassesRoot}\{target}\shell\PowerLink";
            if (!targetsWithVerbs.Contains(target))
            {
                DeleteSubKeyTree(parentPath);
                continue;
            }

            using var k = Registry.CurrentUser.CreateSubKey(parentPath, writable: true)
                ?? throw new InvalidOperationException($"Failed to create parent verb key: {parentPath}");
            k.SetValue("MUIVerb", "PowerLink", RegistryValueKind.String);
            if (icon is not null) k.SetValue("Icon", icon, RegistryValueKind.String);
            // SubCommands="" + ExtendedSubCommandsKey together tell the shell to
            // cascade this verb into the store rather than treat it as a single
            // invocable verb.
            k.SetValue("SubCommands", string.Empty, RegistryValueKind.String);
            k.SetValue("ExtendedSubCommandsKey", store, RegistryValueKind.String);
        }
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

    /// <summary>
    /// Force-clears Explorer's icon cache. Mostly useful during overlay-handler
    /// development — Win10/11 caches rendered overlays aggressively, so a
    /// rebuilt DLL keeps showing old badges until the cache is wiped.
    /// Returns the number of cache files deleted.
    /// </summary>
    public static int ClearIconCache()
    {
        // Cache db files are locked while explorer.exe runs.
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
        }
        catch { }

        var deleted = 0;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (TryDeleteFile(Path.Combine(local, "IconCache.db"))) deleted++;

        var explorerCacheDir = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        if (Directory.Exists(explorerCacheDir))
        {
            // iconcache_*.db only — never touch thumbcache_*.db, which holds
            // user-content thumbnails (photos, videos) and is expensive to
            // rebuild while being unrelated to overlay handlers.
            try
            {
                foreach (var f in Directory.EnumerateFiles(explorerCacheDir, "iconcache_*.db"))
                    if (TryDeleteFile(f)) deleted++;
            }
            catch { }
        }

        // Windows will respawn explorer.exe on its own.
        return deleted;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; /* locked — best effort */ }
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
