using System.Diagnostics;

namespace PowerLink.App.Services;

// HKLM overlay-handler registration stores an absolute path to the shell
// extension DLL. Velopack updates land in a fresh app-X.Y.Z\ folder each
// time, so any path that points inside that folder goes stale immediately
// and the user has to re-register through UAC. We sidestep that by
// registering against a stable junction at %LocalAppData%\PowerLink\shell\
// and retargeting the junction from Velopack's OnFirstRun / OnAppRestarted
// hooks. NTFS folder junctions don't need admin (unlike symlinks), so the
// retarget runs silently in the same process Velopack restarted.
public static class VelopackShellShim
{
    public static string StableShellExtDir { get; }
        = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerLink", "shell");

    public static string StableShellExtDllPath { get; }
        = Path.Combine(StableShellExtDir, "PowerLink.ShellExt.dll");

    public static void EnsureJunction(string currentAppFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAppFolder);
        if (!Directory.Exists(currentAppFolder))
            throw new DirectoryNotFoundException(
                $"Cannot create junction — target folder does not exist: {currentAppFolder}");

        var parent = Path.GetDirectoryName(StableShellExtDir);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (Directory.Exists(StableShellExtDir))
        {
            if (IsJunctionPointingTo(StableShellExtDir, currentAppFolder))
                return;

            // Junction points delete via Directory.Delete without `recursive`
            // because the delete unlinks the reparse point, not the target's
            // contents. For a real directory this would only succeed if it's
            // empty — which is what we want, since we never want to silently
            // wipe a populated non-junction folder a user might have placed
            // here by hand.
            Directory.Delete(StableShellExtDir, recursive: false);
        }

        // mklink is a cmd.exe builtin (not a standalone exe), so we have to
        // route through cmd /c rather than spawn it directly.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{StableShellExtDir}\" \"{currentAppFolder}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start cmd.exe for mklink.");
        // Drain stdout and stderr concurrently before WaitForExit. Sequencing
        // ReadToEnd on both pipes deadlocks if cmd ever fills the second pipe's
        // buffer while we're blocked on the first — rare with mklink's tiny
        // output, fatal during install/update hooks where there's no UI.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"mklink /J failed (exit {proc.ExitCode}) creating junction " +
                $"'{StableShellExtDir}' -> '{currentAppFolder}'. " +
                $"stdout: {stdout.Trim()} stderr: {stderr.Trim()}");
    }

    private static bool IsJunctionPointingTo(string path, string desiredTarget)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                return false;

            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            if (resolved is null) return false;

            return string.Equals(
                Path.GetFullPath(resolved.FullName).TrimEnd('\\', '/'),
                Path.GetFullPath(desiredTarget).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
