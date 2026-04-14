using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PowerLink.App.Pages;

namespace PowerLink.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "PowerLink";
        ContentFrame.Navigate(typeof(DedupPage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            switch (tag)
            {
                case "dedup":
                    ContentFrame.Navigate(typeof(DedupPage));
                    break;
                case "clone":
                    ContentFrame.Navigate(typeof(ClonePage));
                    break;
            }
        }
    }
}
