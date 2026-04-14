using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class DedupPage : Page
{
    public DedupViewModel ViewModel { get; } = new();

    public DedupPage()
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

    private void OnExecuteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.ExecuteDedupCommand.CanExecute(null))
            ViewModel.ExecuteDedupCommand.Execute(null);
        args.Handled = true;
    }
}
