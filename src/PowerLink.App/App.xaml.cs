using Microsoft.UI.Xaml;

namespace PowerLink.App;

public enum LaunchMode { Dedup, Inspect, Clone }

public sealed record LaunchPreset(LaunchMode Mode, string Path);

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    // Populated in OnLaunched when the App was started with --dedup / --inspect /
    // --clone <path> by a shell verb. MainWindow reads this once at construction.
    public static LaunchPreset? Preset { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Preset = ParsePreset(Environment.GetCommandLineArgs());
        MainAppWindow = new MainWindow();
        MainAppWindow.Activate();
    }

    private static LaunchPreset? ParsePreset(string[] argv)
    {
        // argv[0] is the exe path; find the first mode flag followed by a path.
        for (var i = 1; i < argv.Length - 1; i++)
        {
            LaunchMode? mode = argv[i] switch
            {
                "--dedup" => LaunchMode.Dedup,
                "--inspect" => LaunchMode.Inspect,
                "--clone" => LaunchMode.Clone,
                _ => null,
            };
            if (mode is not null)
                return new LaunchPreset(mode.Value, argv[i + 1]);
        }
        return null;
    }
}
