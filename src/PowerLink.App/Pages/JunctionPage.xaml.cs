using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class JunctionPage : Page
{
    public JunctionPageViewModel ViewModel { get; } = new();

    public JunctionPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }
}
