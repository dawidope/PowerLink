using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PowerLink.App.Services;
using PowerLink.Core.State;

namespace PowerLink.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public ObservableCollection<ShellVerbViewModel> Verbs { get; } = new();

    [ObservableProperty] private string _cliPath = string.Empty;
    [ObservableProperty] private string _appPath = string.Empty;
    [ObservableProperty] private string? _pickedPathText;
    [ObservableProperty] private string? _operationStatus;

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
    private void RestartExplorer()
    {
        ShellExtensionService.RestartExplorer();
        OperationStatus = "Explorer restarted.";
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
