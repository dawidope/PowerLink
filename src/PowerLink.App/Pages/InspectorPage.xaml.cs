using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class InspectorPage : Page
{
    public InspectorViewModel ViewModel { get; } = new();

    public InspectorPage()
    {
        InitializeComponent();
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
