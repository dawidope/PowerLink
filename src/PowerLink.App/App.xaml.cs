using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
// Alias the WinRT activation args to avoid the clash with
// Microsoft.UI.Xaml.LaunchActivatedEventArgs used by OnLaunched.
using WinRtActivation = Windows.ApplicationModel.Activation;

namespace PowerLink.App;

public enum LaunchMode { Dedup, Inspect, Clone, Junction }

public sealed record LaunchPreset(LaunchMode Mode, string Path);

public partial class App : Application
{
    private static readonly List<MainWindow> _windows = new();
    private DispatcherQueue? _dispatcher;

    public static MainWindow? MainAppWindow => _windows.LastOrDefault();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        if (Program.PrimaryInstance is not null)
            Program.PrimaryInstance.Activated += OnAppInstanceActivated;

        var preset = ParsePreset(Environment.GetCommandLineArgs());
        OpenNewWindow(preset);
    }

    // Subsequent launches (Start menu click, shell verb) redirect here via
    // AppInstance. Shell verbs that carry a preset get a fresh MainWindow so
    // they don't trample an in-flight scan on the existing one; plain launches
    // just raise the most recent window.
    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            var preset = ExtractPreset(args);
            if (preset is not null)
                OpenNewWindow(preset);
            else
                FocusMostRecent();
        });
    }

    private void OpenNewWindow(LaunchPreset? preset)
    {
        var window = new MainWindow(preset);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            // Unpackaged WinUI keeps the dispatcher running even after the
            // last window closes — explicitly exit so the process ends when
            // the user has no PowerLink windows left.
            if (_windows.Count == 0) Exit();
        };
        _windows.Add(window);
        window.Activate();
    }

    private static void FocusMostRecent()
    {
        var window = _windows.LastOrDefault();
        if (window is null) return;
        window.Activate();
        // Activate() alone doesn't steal focus from another foreground app.
        // ShowWindow(SW_RESTORE) + SetForegroundWindow reliably surface the
        // window over whatever the user was doing.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private static LaunchPreset? ExtractPreset(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.Launch) return null;
        if (args.Data is not WinRtActivation.ILaunchActivatedEventArgs launch) return null;
        if (string.IsNullOrWhiteSpace(launch.Arguments)) return null;

        // ILaunchActivatedEventArgs.Arguments omits the exe path, but our
        // ParsePreset expects Environment.GetCommandLineArgs()-style input
        // where argv[0] is the exe. Prepend a sentinel so the indexing holds.
        var argv = ParseCommandLine("exe " + launch.Arguments);
        return ParsePreset(argv);
    }

    private static LaunchPreset? ParsePreset(string[] argv)
    {
        for (var i = 1; i < argv.Length - 1; i++)
        {
            LaunchMode? mode = argv[i] switch
            {
                "--dedup" => LaunchMode.Dedup,
                "--inspect" => LaunchMode.Inspect,
                "--clone" => LaunchMode.Clone,
                "--junction" => LaunchMode.Junction,
                _ => null,
            };
            if (mode is not null)
                return new LaunchPreset(mode.Value, argv[i + 1]);
        }
        return null;
    }

    private static string[] ParseCommandLine(string cmdLine)
    {
        if (string.IsNullOrEmpty(cmdLine)) return Array.Empty<string>();
        var ptr = CommandLineToArgvW(cmdLine, out int argc);
        if (ptr == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                var strPtr = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(strPtr) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;
}
