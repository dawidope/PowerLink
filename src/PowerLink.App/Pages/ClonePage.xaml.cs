using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class ClonePage : Page, IRelaunchState
{
    bool IRelaunchState.IsBusy => ViewModel.IsRunning;

    LaunchPreset? IRelaunchState.CaptureLaunchPreset() =>
        string.IsNullOrWhiteSpace(ViewModel.SourcePath)
            ? null
            : new LaunchPreset(LaunchMode.Clone, new[] { ViewModel.SourcePath });

    public CloneViewModel ViewModel { get; } = new();

    public ClonePage()
    {
        InitializeComponent();
        // Cache the page so navigating away mid-clone doesn't orphan the
        // ViewModel (work would keep running in the background but the UI
        // would lose all visibility into it).
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is IReadOnlyList<string> { Count: > 0 } paths
            && paths[0] is var path
            && (Directory.Exists(path) || File.Exists(path)))
        {
            ViewModel.SourcePath = path;
        }
    }

    private void OnRunAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.RunCommand.CanExecute(null))
            ViewModel.RunCommand.Execute(null);
        args.Handled = true;
    }

    private void OnStopAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CancelCommand.CanExecute(null))
            ViewModel.CancelCommand.Execute(null);
        args.Handled = true;
    }
}
