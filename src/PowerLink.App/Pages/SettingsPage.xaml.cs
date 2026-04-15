using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        // Cache the page so the user's pending checkbox edits aren't lost
        // when navigating away. OnNavigatedTo still calls Refresh() to pick
        // up registry changes made externally.
        NavigationCacheMode = NavigationCacheMode.Required;

        // Wire the VM's slot-war confirmation callback to a ContentDialog here
        // (ContentDialog needs a XamlRoot, which only the page can supply).
        ViewModel.ConfirmOverlaySlotWar = ShowOverlaySlotWarDialogAsync;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private async Task<bool> ShowOverlaySlotWarDialogAsync(IReadOnlyList<string> handlers)
    {
        var list = string.Join(Environment.NewLine,
            handlers.Select((h, i) => $"  {i + 1,2}. {h}"));

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Overlay slot limit",
            Content =
                $"Windows loads only the first 15 shell-icon overlay handlers in alphabetical order. "
                + $"Your system already has {handlers.Count} registered:"
                + Environment.NewLine + Environment.NewLine
                + list
                + Environment.NewLine + Environment.NewLine
                + "PowerLink registers itself with a leading-space name so it sorts first — but a "
                + "handler sorting after position 15 will be silently dropped. Install anyway?",
            PrimaryButtonText = "Install overlay",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
