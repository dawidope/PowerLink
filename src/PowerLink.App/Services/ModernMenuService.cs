using Microsoft.Win32;
using Windows.Management.Deployment;

namespace PowerLink.App.Services;

/// <summary>
/// Registers PowerLink's Windows 11 top-section ("modern") context menu
/// via PackageManager. No signing required in Developer Mode — the manifest
/// lives next to the app and is registered as a layout package.
/// </summary>
public static class ModernMenuService
{
    // Must match Identity/@Name in AppxManifest.xml.
    public const string PackageName = "PowerLink.ModernMenu";

    private const string AppModelUnlockKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";

    public static string DetectManifestPath()
    {
        var baseDir = AppContext.BaseDirectory;

        // Production layout — manifest sits next to the running exe.
        var sameFolder = Path.Combine(baseDir, "AppxManifest.xml");
        if (File.Exists(sameFolder)) return sameFolder;

        // Development layout — walk up to find the repo copy.
        for (var up = 3; up <= 6; up++)
        {
            var segments = new string[up + 1];
            segments[0] = baseDir;
            for (var i = 1; i <= up; i++) segments[i] = "..";
            var ancestor = Path.GetFullPath(Path.Combine(segments));

            var candidate = Path.Combine(ancestor, "PowerLink.App", "AppxManifest.xml");
            if (File.Exists(candidate)) return candidate;
        }

        return sameFolder;
    }

    /// <summary>
    /// Windows Developer Mode (Settings → For developers) flips this value to 1.
    /// Required for registering unsigned packages via RegisterPackageAsync with
    /// DevelopmentMode — otherwise the call fails with 0x80073CFD.
    /// </summary>
    public static bool IsDeveloperModeEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AppModelUnlockKey);
            if (key is null) return false;
            return key.GetValue("AllowDevelopmentWithoutDevLicense") is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsInstalled()
    {
        try
        {
            var pm = new PackageManager();
            foreach (var p in pm.FindPackagesForUser(string.Empty))
            {
                if (string.Equals(p.Id.Name, PackageName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task InstallAsync()
    {
        var manifest = DetectManifestPath();
        if (!File.Exists(manifest))
            throw new FileNotFoundException("AppxManifest.xml not found.", manifest);

        var pm = new PackageManager();
        var op = pm.RegisterPackageAsync(
            new Uri(manifest),
            dependencyPackageUris: null,
            DeploymentOptions.DevelopmentMode);

        var result = await op.AsTask();
        if (result.IsRegistered) return;

        throw new InvalidOperationException(
            $"Register failed: {result.ErrorText} (0x{result.ExtendedErrorCode.HResult:X8})");
    }

    public static async Task UninstallAsync()
    {
        var pm = new PackageManager();
        string? fullName = null;
        foreach (var p in pm.FindPackagesForUser(string.Empty))
        {
            if (string.Equals(p.Id.Name, PackageName, StringComparison.OrdinalIgnoreCase))
            {
                fullName = p.Id.FullName;
                break;
            }
        }
        if (fullName is null) return;

        var op = pm.RemovePackageAsync(fullName);
        var result = await op.AsTask();
        if (!string.IsNullOrEmpty(result.ErrorText))
            throw new InvalidOperationException(
                $"Remove failed: {result.ErrorText} (0x{result.ExtendedErrorCode.HResult:X8})");
    }
}
