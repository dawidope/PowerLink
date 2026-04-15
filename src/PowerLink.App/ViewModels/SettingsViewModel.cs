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

    [ObservableProperty] private string _cliPath = string.Empty;
    [ObservableProperty] private string _appPath = string.Empty;
    [ObservableProperty] private string? _pickedPathText;
    [ObservableProperty] private string? _operationStatus;
    [ObservableProperty] private string? _overlayStatus;

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
        Refresh();
    }

    public void Refresh()
    {
        CliPath = ShellExtensionService.DetectCliPath();
        AppPath = ShellExtensionService.DetectAppPath();

        foreach (var v in Verbs)
        {
            var installed = ShellExtensionService.IsInstalled(v.Verb);
            v.IsInstalled = installed;
            v.ShouldInstall = installed;
        }

        foreach (var v in OverlayVerbs)
        {
            var installed = ShellExtensionService.IsInstalled(v.Verb);
            v.IsInstalled = installed;
            v.ShouldInstall = installed;
        }

        var picked = PickedSourceStore.TryLoad();
        HasPicked = picked is not null;
        PickedPathText = picked is null
            ? null
            : $"{(picked.IsDirectory ? "Folder" : "File")}: {picked.Path}\nPicked at: {picked.PickedAtUtc.ToLocalTime():g}";
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
    private void RestartExplorer()
    {
        ShellExtensionService.RestartExplorer();
        OperationStatus = "Explorer restarted.";
        OverlayStatus = "Explorer restarted.";
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
