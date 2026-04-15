using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PowerLink.Core.Native;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

/// <summary>
/// End-to-end check that PowerLink.ShellExt.dll's overlay handler answers
/// IsMemberOf correctly. We load the DLL with LoadLibrary + DllGetClassObject
/// directly — no HKLM registration, no CoCreateInstance — so the test is
/// hermetic and doesn't touch the real system-wide overlay slot allocation.
///
/// Tests skip silently when the DLL can't be located (the C++ project has not
/// been built, or this is being run on a non-Windows CI agent). A built Debug
/// or Release DLL under src\PowerLink.ShellExt\x64\&lt;config&gt;\ is the
/// expected precondition.
/// </summary>
[Trait("Category", "Integration")]
[SupportedOSPlatform("windows")]
public class ShellExtIntegrationTests
{
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [Fact]
    public void IsMemberOf_HardlinkedFile_ReturnsSOK()
    {
        var dllPath = FindShellExtDll();
        if (dllPath is null) return;

        using var temp = new TempDirectory();
        var original = temp.CreateFile("file.bin", new byte[] { 1, 2, 3 });
        var link = Path.Combine(temp.Path, "link.bin");
        Win32Hardlink.CreateHardLink(link, original);

        using var host = new OverlayHandlerHost(dllPath);
        var hr = host.Handler.IsMemberOf(original, FILE_ATTRIBUTE_NORMAL);

        Assert.Equal(S_OK, hr);
    }

    [Fact]
    public void IsMemberOf_RegularFile_ReturnsSFalse()
    {
        var dllPath = FindShellExtDll();
        if (dllPath is null) return;

        using var temp = new TempDirectory();
        var regular = temp.CreateFile("regular.bin", new byte[] { 4, 5, 6 });

        using var host = new OverlayHandlerHost(dllPath);
        var hr = host.Handler.IsMemberOf(regular, FILE_ATTRIBUTE_NORMAL);

        Assert.Equal(S_FALSE, hr);
    }

    [Fact]
    public void IsMemberOf_Directory_ReturnsSFalseViaAttributeFastPath()
    {
        var dllPath = FindShellExtDll();
        if (dllPath is null) return;

        using var temp = new TempDirectory();
        using var host = new OverlayHandlerHost(dllPath);

        // Directories always return S_FALSE without opening a handle — the
        // FILE_ATTRIBUTE_DIRECTORY bit short-circuits IsHardlink. This test
        // proves the fast-path reject path stays intact under refactors.
        var hr = host.Handler.IsMemberOf(temp.Path, FILE_ATTRIBUTE_DIRECTORY);

        Assert.Equal(S_FALSE, hr);
    }

    [Fact]
    public void GetPriority_ReturnsConfiguredLowPriority()
    {
        var dllPath = FindShellExtDll();
        if (dllPath is null) return;

        using var host = new OverlayHandlerHost(dllPath);
        var hr = host.Handler.GetPriority(out var priority);

        Assert.Equal(S_OK, hr);
        // Handler ships at low priority (50) so OneDrive/Dropbox overlays win
        // on files those services also claim. If this changes, the constant
        // lives in HardlinkOverlayHandler::GetPriority.
        Assert.Equal(50, priority);
    }

    private static string? FindShellExtDll()
    {
        var baseDir = AppContext.BaseDirectory;
        // Test runner sits at tests\PowerLink.Core.Tests\bin\Debug\net8.0 —
        // walk up to the repo root and then into the ShellExt build output.
        for (var up = 3; up <= 6; up++)
        {
            var segments = new string[up + 1];
            segments[0] = baseDir;
            for (var i = 1; i <= up; i++) segments[i] = "..";
            var ancestor = Path.GetFullPath(Path.Combine(segments));

            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    ancestor, "src", "PowerLink.ShellExt", "x64", config, "PowerLink.ShellExt.dll");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

/// <summary>
/// Loads PowerLink.ShellExt.dll, activates the overlay handler's CLSID via
/// DllGetClassObject + IClassFactory.CreateInstance, and exposes the resulting
/// IShellIconOverlayIdentifier. Dispose releases both RCWs and frees the DLL.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OverlayHandlerHost : IDisposable
{
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000001-0000-0000-C000-000000000046")]
    internal interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(
            [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
            ref Guid riid,
            out IntPtr ppvObject);

        [PreserveSig]
        int LockServer(bool fLock);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0C6C4200-C589-11D0-999A-00C04FD655E1")]
    public interface IShellIconOverlayIdentifier
    {
        [PreserveSig]
        int IsMemberOf([MarshalAs(UnmanagedType.LPWStr)] string pwszPath, uint dwAttrib);

        [PreserveSig]
        int GetOverlayInfo(IntPtr pwszIconFile, int cchMax, out int pIndex, out uint pdwFlags);

        [PreserveSig]
        int GetPriority(out int pPriority);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObjectDelegate(ref Guid rclsid, ref Guid riid, out IntPtr ppv);

    private readonly IntPtr _hModule;
    private IClassFactory? _factory;
    private IShellIconOverlayIdentifier? _handler;

    public IShellIconOverlayIdentifier Handler =>
        _handler ?? throw new ObjectDisposedException(nameof(OverlayHandlerHost));

    public OverlayHandlerHost(string dllPath)
    {
        _hModule = LoadLibraryW(dllPath);
        if (_hModule == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"LoadLibraryW '{dllPath}'");

        var proc = GetProcAddress(_hModule, "DllGetClassObject");
        if (proc == IntPtr.Zero)
            throw new InvalidOperationException("DllGetClassObject export missing.");

        var dgco = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(proc);

        // CLSID must match CLSID_HardlinkOverlayHandler in HardlinkOverlayHandler.h.
        var clsid = new Guid("8E62D9DE-27D3-4C1E-8A55-CADF97D3EB20");
        var iidCf = typeof(IClassFactory).GUID;
        var iidHandler = typeof(IShellIconOverlayIdentifier).GUID;

        var hr = dgco(ref clsid, ref iidCf, out var cfPtr);
        if (hr != 0)
            throw new InvalidOperationException($"DllGetClassObject returned 0x{hr:X8}.");
        try
        {
            _factory = (IClassFactory)Marshal.GetObjectForIUnknown(cfPtr);
        }
        finally
        {
            Marshal.Release(cfPtr);
        }

        hr = _factory.CreateInstance(null, ref iidHandler, out var hPtr);
        if (hr != 0)
            throw new InvalidOperationException($"IClassFactory.CreateInstance returned 0x{hr:X8}.");
        try
        {
            _handler = (IShellIconOverlayIdentifier)Marshal.GetObjectForIUnknown(hPtr);
        }
        finally
        {
            Marshal.Release(hPtr);
        }
    }

    public void Dispose()
    {
        if (_handler is not null) { Marshal.ReleaseComObject(_handler); _handler = null; }
        if (_factory is not null) { Marshal.ReleaseComObject(_factory); _factory = null; }
        if (_hModule != IntPtr.Zero) FreeLibrary(_hModule);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);
}
