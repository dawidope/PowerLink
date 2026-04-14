using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class ClonePage : Page
{
    public CloneViewModel ViewModel { get; } = new();

    public ClonePage()
    {
        InitializeComponent();
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
