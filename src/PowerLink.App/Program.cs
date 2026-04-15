using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PowerLink.App;

public static class Program
{
    // The Activated-subscription handle the running App uses to receive
    // forwarded launches from subsequent PowerLink.App.exe invocations.
    public static AppInstance? PrimaryInstance;

    [STAThread]
    private static int Main(string[] args)
    {
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
}
