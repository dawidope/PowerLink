using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class DedupPage : Page, IRelaunchState
{
    bool IRelaunchState.IsBusy => ViewModel.IsScanning || ViewModel.IsExecuting;

    LaunchPreset? IRelaunchState.CaptureLaunchPreset() =>
        ViewModel.Paths.Count == 0 ? null : new LaunchPreset(LaunchMode.Dedup, ViewModel.Paths.ToList());

    public DedupViewModel ViewModel { get; } = new();

    public DedupPage()
    {
        InitializeComponent();
        // Cache the page so navigating away mid-scan doesn't orphan the
        // ViewModel (work would keep running in the background but the UI
        // would lose all visibility into it).
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not IReadOnlyList<string> paths) return;
        foreach (var path in paths)
        {
            if (Directory.Exists(path) && !ViewModel.Paths.Contains(path))
                ViewModel.Paths.Add(path);
        }
    }

    private void OnRestartAsAdminClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _ = App.WindowFor(this)?.RestartAsAdministratorAsync();
    }

    private void OnScanAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.ScanCommand.CanExecute(null))
            ViewModel.ScanCommand.Execute(null);
        args.Handled = true;
    }

    private void OnStopAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CancelCommand.CanExecute(null))
            ViewModel.CancelCommand.Execute(null);
        args.Handled = true;
    }

    private void OnExecuteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.ExecuteDedupCommand.CanExecute(null))
            ViewModel.ExecuteDedupCommand.Execute(null);
        args.Handled = true;
    }
}
