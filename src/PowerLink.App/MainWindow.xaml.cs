using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PowerLink.App.Pages;

namespace PowerLink.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "PowerLink";
        SetWindowIcon();
        SetMicaBackdrop();
        ContentFrame.Navigate(typeof(DedupPage));
    }

    private void SetMicaBackdrop()
    {
        // Mica is supported on Windows 11; on older versions the property
        // assignment is a no-op (graceful degradation to opaque background).
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
    }

    private void SetWindowIcon()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icon.ico");
            if (File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }
        catch
        {
            // Non-fatal — fall back to default icon.
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        Type? target = tag switch
        {
            "dedup" => typeof(DedupPage),
            "clone" => typeof(ClonePage),
            "inspector" => typeof(InspectorPage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };

        // Skip re-navigation to the current page — Navigate() creates a new
        // Page + ViewModel each time, orphaning any scan already in flight.
        if (target is null || ContentFrame.SourcePageType == target) return;
        ContentFrame.Navigate(target);
    }

    private void ThemeToggleItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (Content is FrameworkElement root)
        {
            var current = root.ActualTheme;
            var next = current == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = next;
            ThemeToggleIcon.Glyph = next == ElementTheme.Dark ? "\uE706" : "\uE708";
            ThemeToggleItem.Content = next == ElementTheme.Dark ? "Light mode" : "Dark mode";
        }
    }
}
