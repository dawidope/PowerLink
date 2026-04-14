using Microsoft.UI.Xaml.Controls;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class DedupPage : Page
{
    public DedupViewModel ViewModel { get; } = new();

    public DedupPage()
    {
        InitializeComponent();
    }
}
