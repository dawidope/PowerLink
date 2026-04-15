using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty] private string _cliPath = string.Empty;
    [ObservableProperty] private string _appPath = string.Empty;
    [ObservableProperty] private string? _pickedPathText;
    [ObservableProperty] private string? _operationStatus;
    [ObservableProperty] private string? _overlayStatus;
    [ObservableProperty] private string? _dropStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickedVisibility))]
    [NotifyPropertyChangedFor(nameof(NoPickVisibility))]
    [NotifyCanExecuteChangedFor(nameof(ClearPickCommand))]
    private bool _hasPicked;

    public Visibility PickedVisibility => HasPicked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoPickVisibility => HasPicked ? Visibility.Collapsed : Visibility.Visible;

    public SettingsViewModel()
    {
        foreach (var verb in ShellExtensionService.AllVerbs)
            Verbs.Add(new ShellVerbViewModel { Verb = verb });
        foreach (var verb in ShellExtensionService.OverlayVerbs)
            OverlayVerbs.Add(new ShellVerbViewModel { Verb = verb });
        foreach (var verb in ShellExtensionService.DropVerbs)
            DropVerbs.Add(new ShellVerbViewModel { Verb = verb });
        Refresh();
    }

    public void Refresh()
    {
        CliPath = ShellExtensionService.DetectCliPath();
        AppPath = ShellExtensionService.DetectAppPath();

        SyncVerbStates(Verbs);
        SyncVerbStates(OverlayVerbs);
        SyncVerbStates(DropVerbs);

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

            var installed = 0;
            var uninstalled = 0;
            foreach (var v in Verbs)
            {
                if (v.ShouldInstall && !v.IsInstalled)
                {
                    ShellExtensionService.Install(v.Verb, cli, app, icon);
                    installed++;
                }
                else if (!v.ShouldInstall && v.IsInstalled)
                {
                    ShellExtensionService.Uninstall(v.Verb);
                    uninstalled++;
                }
            }

            Refresh();
            OperationStatus = (installed, uninstalled) switch
            {
                (0, 0) => "No changes.",
                (_, 0) => $"Installed {installed} verb(s). Restart Explorer to see them.",
                (0, _) => $"Uninstalled {uninstalled} verb(s). Restart Explorer to apply.",
                _ => $"Installed {installed}, uninstalled {uninstalled}. Restart Explorer to apply.",
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
            var existing = ShellExtensionService.CountOverlayHandlersOnSystem();
            if (existing >= 14)
                OverlayStatus = $"Note: {existing} overlay handlers already registered. Windows loads only the first 15 alphabetically — PowerLink uses a leading-space name to win the sort.";
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
    private void RestartExplorer()
    {
        ShellExtensionService.RestartExplorer();
        OperationStatus = "Explorer restarted.";
        OverlayStatus = "Explorer restarted.";
        DropStatus = "Explorer restarted.";
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
}
