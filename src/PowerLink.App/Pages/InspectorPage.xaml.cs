using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class InspectorPage : Page
{
    public InspectorViewModel ViewModel { get; } = new();

    public InspectorPage()
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
        if (e.Parameter is string path
            && Directory.Exists(path)
            && !ViewModel.Paths.Contains(path))
        {
            ViewModel.Paths.Add(path);
        }
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
}
