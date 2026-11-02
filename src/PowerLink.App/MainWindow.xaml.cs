using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PowerLink.App.Pages;
using PowerLink.App.Services;

namespace PowerLink.App;

public sealed partial class MainWindow : Window
{
    private readonly LaunchPreset? _preset;

    public MainWindow() : this(null) { }

    public MainWindow(LaunchPreset? preset)
    {
        _preset = preset;
        InitializeComponent();
        Title = ElevationService.IsElevated ? "PowerLink (Administrator)" : "PowerLink";
        if (ElevationService.IsElevated)
            RestartAsAdminItem.Visibility = Visibility.Collapsed;
        ContentFrame.Navigated += (_, e) =>
        {
            if (e.Content is IRelaunchState page) _visitedPages.Add(page);
        };
        SetWindowIcon();
        SetMicaBackdrop();
        NavigateInitialPage();
    }

    // Pages are cached (NavigationCacheMode.Required), so a scan can keep
    // running on a page the user navigated away from. Track every page
    // instance we've shown so the busy check covers all of them.
    private readonly HashSet<IRelaunchState> _visitedPages = new();

    public async Task RestartAsAdministratorAsync()
    {
        if (_visitedPages.Any(p => p.IsBusy))
        {
            await ShowMessageAsync("Operation in progress",
                "Stop the current scan or operation, or wait for it to finish, then try again.");
            return;
        }

        var preset = (ContentFrame.Content as IRelaunchState)?.CaptureLaunchPreset();
        try
        {
            if (!ElevationService.TryStartElevated(preset))
                return; // UAC prompt declined — keep this window as is.
        }
        catch (Exception ex)
        {
            ErrorLog.Write("RestartAsAdministrator", ex);
            await ShowMessageAsync("Couldn't restart as administrator", ex.Message);
            return;
        }

        Close();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private void RestartAsAdminItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _ = RestartAsAdministratorAsync();
    }

    private void NavigateInitialPage()
    {
        var preset = _preset;
        if (preset is null)
        {
            ContentFrame.Navigate(typeof(DedupPage));
            return;
        }

        var (pageType, navTag) = preset.Mode switch
        {
            LaunchMode.Dedup => (typeof(DedupPage), "dedup"),
            LaunchMode.Inspect => (typeof(InspectorPage), "inspector"),
            LaunchMode.Clone => (typeof(ClonePage), "clone"),
            LaunchMode.Junction => (typeof(JunctionPage), "junction"),
            _ => (typeof(DedupPage), "dedup"),
        };

        // Sync NavView selection so the sidebar highlights the chosen page.
        // SuppressNavigate used to avoid double-navigation from SelectionChanged.
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == navTag)
            {
                _suppressNavigate = true;
                Nav.SelectedItem = item;
                _suppressNavigate = false;
                break;
            }
        }

        ContentFrame.Navigate(pageType, preset.Paths);
    }

    private bool _suppressNavigate;

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
        if (_suppressNavigate) return;
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        Type? target = tag switch
        {
            "dedup" => typeof(DedupPage),
            "clone" => typeof(ClonePage),
            "junction" => typeof(JunctionPage),
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
