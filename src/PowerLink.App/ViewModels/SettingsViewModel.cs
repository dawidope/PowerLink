using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Win32Exception = System.ComponentModel.Win32Exception;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PowerLink.App.Services;
using PowerLink.Core.State;

namespace PowerLink.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED — user cancelled UAC prompt

    public ObservableCollection<ShellVerbViewModel> Verbs { get; } = new();
    public ObservableCollection<ShellVerbViewModel> OverlayVerbs { get; } = new();
    public ObservableCollection<ShellVerbViewModel> DropVerbs { get; } = new();
    public ObservableCollection<ShellVerbViewModel> ModernVerbs { get; } = new();

    [ObservableProperty] public partial string CliPath { get; set; }
    [ObservableProperty] public partial string AppPath { get; set; }
    [ObservableProperty] public partial string? PickedPathText { get; set; }
    [ObservableProperty] public partial string? OperationStatus { get; set; }
    [ObservableProperty] public partial string? OverlayStatus { get; set; }
    [ObservableProperty] public partial string? DropStatus { get; set; }
    [ObservableProperty] public partial string? ModernStatus { get; set; }
    [ObservableProperty] public partial bool GroupedLayout { get; set; }

    // Populated in Refresh() with a non-empty message when the current machine
    // can't register the modern menu package (Windows 10 doesn't expose the
    // top-section API at all, and unsigned packages require Developer Mode).
    // Bound to the InfoBar in the Modern menu Settings section.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModernRequirementsVisibility))]
    public partial string? ModernRequirementsWarning { get; set; }

    public Visibility ModernRequirementsVisibility =>
        string.IsNullOrEmpty(ModernRequirementsWarning) ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickedVisibility))]
    [NotifyPropertyChangedFor(nameof(NoPickVisibility))]
    [NotifyCanExecuteChangedFor(nameof(ClearPickCommand))]
    public partial bool HasPicked { get; set; }

    public Visibility PickedVisibility => HasPicked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoPickVisibility => HasPicked ? Visibility.Collapsed : Visibility.Visible;

    // Injected by SettingsPage — returns true if the user confirmed installing
    // the overlay despite the system being at/over Windows' 15-handler limit.
    // Kept as a simple Func to avoid dragging a view reference into the VM.
    public Func<IReadOnlyList<string>, Task<bool>>? ConfirmOverlaySlotWar { get; set; }

    public SettingsViewModel()
    {
        // Defensive defaults: Refresh() below replaces both with detected
        // paths, but a partial-property without an implicit-default init
        // would leave them null until then.
        CliPath = string.Empty;
        AppPath = string.Empty;

        foreach (var verb in ShellExtensionService.AllVerbs)
            Verbs.Add(new ShellVerbViewModel { Verb = verb });
        foreach (var verb in ShellExtensionService.OverlayVerbs)
            OverlayVerbs.Add(new ShellVerbViewModel { Verb = verb });
        foreach (var verb in ShellExtensionService.DropVerbs)
            DropVerbs.Add(new ShellVerbViewModel { Verb = verb });
        foreach (var verb in ShellExtensionService.ModernMenuVerbs)
            ModernVerbs.Add(new ShellVerbViewModel { Verb = verb });
        Refresh();
    }

    public void Refresh()
    {
        CliPath = ShellExtensionService.DetectCliPath();
        AppPath = ShellExtensionService.DetectAppPath();
        GroupedLayout = ShellExtensionService.GetLayout() == ContextMenuLayout.Grouped;
        ModernRequirementsWarning = ComputeModernRequirementsWarning();

        SyncVerbStates(Verbs);
        SyncVerbStates(OverlayVerbs);
        SyncVerbStates(DropVerbs);
        SyncVerbStates(ModernVerbs);

        var picked = PickedSourceStore.TryLoad();
        HasPicked = picked is not null;
        PickedPathText = picked is null
            ? null
            : $"{(picked.IsDirectory ? "Folder" : "File")}: {picked.Path}\nPicked at: {picked.PickedAtUtc.ToLocalTime():g}";
    }

    /// <summary>
    /// Re-reads each verb's installed state from the registry. If the user
    /// has a pending checkbox change (ShouldInstall != IsInstalled before
    /// the read), we preserve their intent rather than overwriting it —
    /// otherwise the page-cached Settings page would lose pending edits
    /// every time the user navigates away and back.
    /// </summary>
    private static void SyncVerbStates(System.Collections.Generic.IEnumerable<ShellVerbViewModel> verbs)
    {
        foreach (var v in verbs)
        {
            var oldInstalled = v.IsInstalled;
            var userHasPendingChange = v.ShouldInstall != oldInstalled;

            var newInstalled = ShellExtensionService.IsInstalled(v.Verb);
            v.IsInstalled = newInstalled;
            if (!userHasPendingChange)
                v.ShouldInstall = newInstalled;
        }
    }

    [RelayCommand]
    private void ApplyChanges()
    {
        try
        {
            var cli = ShellExtensionService.DetectCliPath();
            var app = ShellExtensionService.DetectAppPath();
            var icon = ShellExtensionService.DetectIconPath();

            var oldLayout = ShellExtensionService.GetLayout();
            var newLayout = GroupedLayout ? ContextMenuLayout.Grouped : ContextMenuLayout.Flat;

            // Capture diff before Apply rewrites state so the status message
            // reflects the user's actual delta, not the post-write reality.
            var installing = Verbs.Count(v => v.ShouldInstall && !v.IsInstalled);
            var uninstalling = Verbs.Count(v => !v.ShouldInstall && v.IsInstalled);

            var selection = Verbs.Select(v => (v.Verb, v.ShouldInstall)).ToList();
            ShellExtensionService.ApplyContextMenuSelection(selection, cli, app, icon, newLayout);

            Refresh();
            var layoutChanged = oldLayout != newLayout;
            OperationStatus = (installing, uninstalling, layoutChanged) switch
            {
                (0, 0, false) => "No changes.",
                (0, 0, true)  => $"Layout switched to {newLayout}. Restart Explorer to apply.",
                (_, 0, _)     => $"Installed {installing} verb(s). Restart Explorer to see them.",
                (0, _, _)     => $"Uninstalled {uninstalling} verb(s). Restart Explorer to apply.",
                _             => $"Installed {installing}, uninstalled {uninstalling}. Restart Explorer to apply.",
            };
        }
        catch (Exception ex)
        {
            OperationStatus = $"Apply failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void UninstallAll()
    {
        try
        {
            ShellExtensionService.UninstallAll();
            Refresh();
            OperationStatus = "All verbs uninstalled. Restart Explorer to apply.";
        }
        catch (Exception ex)
        {
            OperationStatus = $"Uninstall failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyOverlayChangesAsync()
    {
        var install = OverlayVerbs.Any(v => v.ShouldInstall && !v.IsInstalled);
        var uninstall = OverlayVerbs.Any(v => !v.ShouldInstall && v.IsInstalled);
        if (!install && !uninstall)
        {
            OverlayStatus = "No changes.";
            return;
        }

        var dllPath = ShellExtensionService.DetectShellExtDllPath();
        if (install && !File.Exists(dllPath))
        {
            OverlayStatus = $"PowerLink.ShellExt.dll not found at {dllPath}. Build the C++ project first.";
            return;
        }

        if (install)
        {
            var handlers = ShellExtensionService.ListOverlayHandlers();
            // 14 rather than 15: adding ours would push the total to 15+, right
            // at the limit where Windows starts ignoring handlers. Ask first so
            // the user sees whose slot they might steal.
            if (handlers.Count >= 14 && ConfirmOverlaySlotWar is not null)
            {
                var proceed = await ConfirmOverlaySlotWar(handlers);
                if (!proceed)
                {
                    OverlayStatus = "Cancelled — overlay install not attempted.";
                    return;
                }
            }
        }

        var args = install
            ? $"install-overlay \"{dllPath}\""
            : "uninstall-overlay";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ShellExtensionService.DetectCliPath(),
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                OverlayStatus = "Cancelled.";
                return;
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                OverlayStatus = $"Overlay {(install ? "install" : "uninstall")} failed (exit {process.ExitCode}).";
                Refresh();
                return;
            }

            Refresh();
            OverlayStatus = install
                ? "Overlay installed. Restart Explorer to see the badge."
                : "Overlay uninstalled. Restart Explorer to remove the badge.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            OverlayStatus = "Cancelled — overlay handler needs admin permission.";
            Refresh();
        }
        catch (Exception ex)
        {
            OverlayStatus = $"Overlay change failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ApplyDropChanges()
    {
        var install = DropVerbs.Any(v => v.ShouldInstall && !v.IsInstalled);
        var uninstall = DropVerbs.Any(v => !v.ShouldInstall && v.IsInstalled);
        if (!install && !uninstall)
        {
            DropStatus = "No changes.";
            return;
        }

        try
        {
            var installed = 0;
            var uninstalled = 0;
            foreach (var v in DropVerbs)
            {
                if (v.ShouldInstall && !v.IsInstalled)
                {
                    var dllPath = ShellExtensionService.DetectShellExtDllPath();
                    if (!File.Exists(dllPath))
                    {
                        DropStatus = $"PowerLink.ShellExt.dll not found at {dllPath}. Build the C++ project first.";
                        return;
                    }
                    ShellExtensionService.InstallDropHandler(dllPath);
                    installed++;
                }
                else if (!v.ShouldInstall && v.IsInstalled)
                {
                    ShellExtensionService.UninstallDropHandler();
                    uninstalled++;
                }
            }
            Refresh();
            DropStatus = (installed, uninstalled) switch
            {
                (_, 0) => "Drop handler installed. Restart Explorer to activate.",
                (0, _) => "Drop handler uninstalled. Restart Explorer to detach.",
                _ => "Applied. Restart Explorer.",
            };
        }
        catch (Exception ex)
        {
            DropStatus = $"Apply failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyModernChangesAsync()
    {
        var install = ModernVerbs.Any(v => v.ShouldInstall && !v.IsInstalled);
        var uninstall = ModernVerbs.Any(v => !v.ShouldInstall && v.IsInstalled);
        if (!install && !uninstall)
        {
            ModernStatus = "No changes.";
            return;
        }

        if (install && !ModernMenuService.IsDeveloperModeEnabled())
        {
            ModernStatus = "Developer Mode is off. Enable it in Settings → Privacy & security → For developers, then try again. (Unsigned packages need Developer Mode.)";
            Refresh();
            return;
        }

        try
        {
            if (install)
            {
                await ModernMenuService.InstallAsync();
                Refresh();
                ModernStatus = "Modern menu registered. Restart Explorer and right-click any file/folder to try it.";
            }
            else
            {
                await ModernMenuService.UninstallAsync();
                Refresh();
                ModernStatus = "Modern menu unregistered. Restart Explorer to remove the entry.";
            }
        }
        catch (Exception ex)
        {
            ModernStatus = $"Modern menu change failed: {ex.Message}";
            Refresh();
        }
    }

    [RelayCommand]
    private void RestartExplorer()
    {
        ShellExtensionService.RestartExplorer();
        OperationStatus = "Explorer restarted.";
        OverlayStatus = "Explorer restarted.";
        DropStatus = "Explorer restarted.";
        ModernStatus = "Explorer restarted.";
    }

    [RelayCommand]
    private void ClearIconCache()
    {
        var deleted = ShellExtensionService.ClearIconCache();
        OverlayStatus = deleted == 0
            ? "No cache files deleted (already clean or locked). Explorer restarting…"
            : $"Cleared {deleted} icon cache file(s). Explorer restarting; first folder open will be slower as the cache rebuilds.";
    }

    [RelayCommand(CanExecute = nameof(CanClearPick))]
    private void ClearPick()
    {
        PickedSourceStore.Clear();
        Refresh();
    }

    [RelayCommand]
    private void RefreshPicked() => Refresh();

    private bool CanClearPick() => HasPicked;

    private static string? ComputeModernRequirementsWarning()
    {
        // Win11 is 10.0.22000+. Earlier builds don't have the top-section
        // fileExplorerContextMenus extension point at all — no amount of
        // Developer Mode will make it light up, so surface that distinctly.
        if (Environment.OSVersion.Version.Build < 22000)
            return "Windows 11 (build 22000 or newer) is required — the top-section context-menu API doesn't exist on Windows 10.";
        if (!ModernMenuService.IsDeveloperModeEnabled())
            return "Windows Developer Mode is off. Enable it in Settings → Privacy & security → For developers so the unsigned sparse package can register.";
        return null;
    }
}
