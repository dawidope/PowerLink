using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using PowerLink.App.Services;
using Velopack;

namespace PowerLink.App;

public static class Program
{
    // The Activated-subscription handle the running App uses to receive
    // forwarded launches from subsequent PowerLink.App.exe invocations.
    public static AppInstance? PrimaryInstance;

    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack hijacks Main when invoked with --veloapp-* args during
        // install / update / uninstall: it runs the matching hook and exits
        // before we ever reach WinUI init. Keep this as the very first call
        // so the AUMID it sets propagates to any windows we open below
        // (otherwise taskbar pin would target the versioned exe rather than
        // Velopack's stable stub launcher).
        VelopackApp.Build()
            .OnFirstRun(_ => TryEnsureShellJunction("OnFirstRun"))
            .OnRestarted(_ => TryEnsureShellJunction("OnRestarted"))
            .Run();

        // Belt-and-suspenders: OnFirstRun and OnRestarted only fire during
        // install/update flows. A normal cold-start with an externally deleted
        // or stale junction (rare, but possible if the user's previous app-X
        // folder was cleaned up by hand) would leave the shell extension
        // pointing at nothing. Re-ensure on every Velopack-managed launch.
        if (VelopackShellShim.IsRunningFromVelopackInstall(AppContext.BaseDirectory))
            TryEnsureShellJunction("ColdStart");

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Capture whatever activation payload launched this process BEFORE any
        // hand-off so we can pass the real args to the primary if we turn out
        // to be secondary.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey("PowerLink.App.SingleInstance");

        if (!instance.IsCurrent)
        {
            // Already running somewhere — forward and exit. The primary's
            // Activated handler decides whether to spawn a new MainWindow
            // (when the args carry a shell preset like --dedup <path>) or
            // simply bring its existing window to the foreground.
            instance.RedirectActivationToAsync(activation).AsTask().Wait();
            return 0;
        }

        PrimaryInstance = instance;
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            new App();
        });
        return 0;
    }

    // EnsureJunction runs from Velopack install/update hooks where there is
    // no UI to surface failures. An uncaught exception in a hook silently
    // aborts the install — much worse than a temporarily missing shell ext.
    // Log to a file the user can find later and let the hook keep going.
    private static void TryEnsureShellJunction(string hookName)
    {
        try
        {
            VelopackShellShim.EnsureJunction(AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PowerLink", "shell-junction-error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath,
                    $"[{DateTime.UtcNow:O}] {hookName} EnsureJunction failed: {ex}{Environment.NewLine}");
            }
            catch
            {
                // Logging itself failed — nothing safe to do from a hook.
            }
        }
    }
}
