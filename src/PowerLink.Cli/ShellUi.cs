using System.Runtime.InteropServices;

namespace PowerLink.Cli;

internal static class ShellUi
{
    // When the CLI is launched from Explorer via a shell verb, it gets its
    // own console window that closes in ~1s — stderr vanishes before the user
    // can read it. In that case, also surface errors as a native MessageBox.
    // When invoked from a terminal, the console process list has >1 entry
    // and we leave stderr alone.
    public static void ReportError(string title, string message)
    {
        Console.Error.WriteLine(message);
        if (IsStandaloneConsole())
            MessageBoxW(IntPtr.Zero, message, title, MB_OK | MB_ICONERROR);
    }

    public static void ReportInfo(string title, string message)
    {
        Console.WriteLine(message);
        if (IsStandaloneConsole())
            MessageBoxW(IntPtr.Zero, message, title, MB_OK | MB_ICONINFORMATION);
    }

    private static bool IsStandaloneConsole()
    {
        try
        {
            var list = new uint[4];
            var count = GetConsoleProcessList(list, (uint)list.Length);
            return count <= 1;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONINFORMATION = 0x00000040;
}
