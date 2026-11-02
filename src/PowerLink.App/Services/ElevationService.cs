using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PowerLink.App.Services;

public static class ElevationService
{
    private const int ErrorCancelled = 1223; // user said "No" on the UAC prompt

    public static bool IsElevated { get; } = DetectElevation();

    // Starts an elevated copy of PowerLink that reopens the given preset.
    // Returns false when the user declines the UAC prompt; other failures
    // throw. The caller closes its own window on success.
    public static bool TryStartElevated(LaunchPreset? preset)
    {
        var exe = Environment.ProcessPath
                  ?? Path.Combine(AppContext.BaseDirectory, "PowerLink.App.exe");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
        };
        // ArgumentList quotes each path correctly, including trailing
        // backslashes like "C:\" that a hand-built string would break.
        if (preset is not null)
            foreach (var arg in preset.ToArguments())
                psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            return process is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    public static string DescribeSkippedFolders(int count)
    {
        var folders = count == 1 ? "1 folder was" : $"{count:N0} folders were";
        return IsElevated
            ? $"{folders} skipped even with administrator rights — usually system-protected " +
              "folders such as System Volume Information. Results don't include them."
            : $"{folders} skipped because PowerLink doesn't have permission to read them. " +
              "Results may be incomplete.";
    }

    private static bool DetectElevation()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
