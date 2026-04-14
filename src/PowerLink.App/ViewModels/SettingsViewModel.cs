using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PowerLink.App.Services;
using PowerLink.Core.State;

namespace PowerLink.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool _isInstalled;

    [ObservableProperty] private string _cliPath = string.Empty;
    [ObservableProperty] private string? _pickedPathText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickedVisibility))]
    [NotifyPropertyChangedFor(nameof(NoPickVisibility))]
    [NotifyCanExecuteChangedFor(nameof(ClearPickCommand))]
    private bool _hasPicked;

    public string StatusText => IsInstalled ? "Installed" : "Not installed";
    public Visibility PickedVisibility => HasPicked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoPickVisibility => HasPicked ? Visibility.Collapsed : Visibility.Visible;

    public SettingsViewModel()
    {
        Refresh();
    }

    public void Refresh()
    {
        IsInstalled = ShellExtensionService.IsInstalled();
        CliPath = ShellExtensionService.DetectCliPath();

        var picked = PickedSourceStore.TryLoad();
        HasPicked = picked is not null;
        PickedPathText = picked is null
            ? null
            : $"{(picked.IsDirectory ? "Folder" : "File")}: {picked.Path}\nPicked at: {picked.PickedAtUtc.ToLocalTime():g}";
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void Install()
    {
        try
        {
            var cli = ShellExtensionService.DetectCliPath();
            var icon = ShellExtensionService.DetectIconPath();
            ShellExtensionService.Install(cli, icon);
            Refresh();
        }
        catch (Exception ex)
        {
            PickedPathText = $"Install failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private void Uninstall()
    {
        try
        {
            ShellExtensionService.Uninstall();
            Refresh();
        }
        catch (Exception ex)
        {
            PickedPathText = $"Uninstall failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RestartExplorer() => ShellExtensionService.RestartExplorer();

    [RelayCommand(CanExecute = nameof(CanClearPick))]
    private void ClearPick()
    {
        PickedSourceStore.Clear();
        Refresh();
    }

    [RelayCommand]
    private void RefreshPicked() => Refresh();

    private bool CanInstall() => !IsInstalled;
    private bool CanUninstall() => IsInstalled;
    private bool CanClearPick() => HasPicked;
}
