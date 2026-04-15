using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace PowerLink.Cli;

[SupportedOSPlatform("windows")]
public static class OverlayInstaller
{
    // Must match CLSID_HardlinkOverlayHandler in PowerLink.ShellExt/HardlinkOverlayHandler.h.
    public const string Clsid = "{8E62D9DE-27D3-4C1E-8A55-CADF97D3EB20}";

    // Leading space wins alphabetical sort — Windows loads only the first 15
    // overlay handlers it sees, so sorting first matters.
    public const string OverlayRegName = " PowerLinkHardlink";

    public const string OverlayRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
    public const string ClsidRoot = @"SOFTWARE\Classes\CLSID";

    private const string HandlerDescription = "PowerLink Hardlink Overlay";

    public static bool IsInstalled()
    {
        using var cls = Registry.LocalMachine.OpenSubKey($@"{ClsidRoot}\{Clsid}\InprocServer32");
        using var overlay = Registry.LocalMachine.OpenSubKey($@"{OverlayRoot}\{OverlayRegName}");
        if (cls is null || overlay is null) return false;

        // Match ShellExtensionService.IsOverlayInstalled — a registration that
        // points at a DLL no longer on disk (build cleaned, binary moved) is
        // not really "installed", and reporting it as such lets re-install
        // logic skip the very write it needs to repair the registration.
        return cls.GetValue(string.Empty) is string dll && File.Exists(dll);
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void Install(string dllPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("Shell extension DLL not found.", dllPath);

        using (var clsid = Registry.LocalMachine.CreateSubKey($@"{ClsidRoot}\{Clsid}", writable: true)
            ?? throw new InvalidOperationException("Failed to create CLSID key."))
        {
            clsid.SetValue(string.Empty, HandlerDescription, RegistryValueKind.String);

            using var inproc = clsid.CreateSubKey("InprocServer32", writable: true)
                ?? throw new InvalidOperationException("Failed to create InprocServer32 key.");
            inproc.SetValue(string.Empty, dllPath, RegistryValueKind.String);
            inproc.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
        }

        using var overlay = Registry.LocalMachine.CreateSubKey(
            $@"{OverlayRoot}\{OverlayRegName}", writable: true)
            ?? throw new InvalidOperationException("Failed to create overlay registration key.");
        overlay.SetValue(string.Empty, Clsid, RegistryValueKind.String);
    }

    public static void Uninstall()
    {
        TryDelete($@"{OverlayRoot}\{OverlayRegName}");
        TryDelete($@"{ClsidRoot}\{Clsid}");
    }

    public static int CountExistingOverlayHandlers()
    {
        using var root = Registry.LocalMachine.OpenSubKey(OverlayRoot);
        return root?.GetSubKeyNames().Length ?? 0;
    }

    private static void TryDelete(string path)
    {
        try { Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
        catch { /* idempotent */ }
    }
}
