namespace PowerLink.App;

// Implemented by pages so "Restart as administrator" can refuse while work
// is running and reopen the elevated copy where the user left off.
public interface IRelaunchState
{
    bool IsBusy { get; }

    LaunchPreset? CaptureLaunchPreset();
}
