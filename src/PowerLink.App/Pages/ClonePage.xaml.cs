using Microsoft.UI.Xaml.Controls;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class ClonePage : Page
{
    public CloneViewModel ViewModel { get; } = new();

    public ClonePage()
    {
        InitializeComponent();
    }
}
