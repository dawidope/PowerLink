using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerLink.App.ViewModels;

namespace PowerLink.App.Pages;

public sealed partial class JunctionPage : Page, IRelaunchState
{
    bool IRelaunchState.IsBusy => false;

    LaunchPreset? IRelaunchState.CaptureLaunchPreset() =>
        string.IsNullOrWhiteSpace(ViewModel.TargetPath)
            ? null
            : new LaunchPreset(LaunchMode.Junction, new[] { ViewModel.TargetPath });

    public JunctionPageViewModel ViewModel { get; } = new();

    public JunctionPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is IReadOnlyList<string> { Count: > 0 } paths
            && paths[0] is var path
            && Directory.Exists(path))
        {
            // Shell ext passes an existing folder — interpret it as the target
            // the user wants this junction to point at. They still have to pick
            // where the junction itself lives.
            ViewModel.TargetPath = path;
            var basename = Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(basename))
                ViewModel.LinkName = basename;
        }
    }
}
