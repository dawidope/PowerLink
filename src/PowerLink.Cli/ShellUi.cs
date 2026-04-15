using System.Runtime.InteropServices;

namespace PowerLink.Cli;

internal static class ShellUi
{
    // When the CLI is launched from Explorer via a shell verb, it gets its
    // own console window that closes in ~1s — stderr vanishes before the user
    // can read it. In that case, also surface the message as a themed
    // TaskDialog (Common Controls v6, DPI-aware, Windows 11 look).
    // When invoked from a terminal, the console process list has >1 entry
    // and stderr/stdout is enough.
    public static void ReportError(string title, string message)
    {
        Console.Error.WriteLine(message);
        if (IsStandaloneConsole())
            ShowDialog(title, message, TD_ERROR_ICON);
    }

    public static void ReportInfo(string title, string message)
    {
        Console.WriteLine(message);
        if (IsStandaloneConsole())
            ShowDialog(title, message, TD_INFORMATION_ICON);
    }

    // TaskDialog renders the first part in a larger "main instruction" font
    // and the rest as smaller selectable body text. We split on the first
    // blank line so callers can opt into that layout by formatting their
    // message as "headline\n\ndetails".
    private static void ShowDialog(string title, string message, IntPtr icon)
    {
        var (instruction, content) = SplitMessage(message);
        try
        {
            TaskDialog(IntPtr.Zero, IntPtr.Zero, title, instruction, content,
                TDCBF_OK_BUTTON, icon, out _);
        }
        catch (Exception)
        {
            // If comctl32 v6 isn't available (shouldn't happen on Win10+ with
            // our manifest), fall back silently. stderr already has the text.
        }
    }

    private static (string instruction, string? content) SplitMessage(string message)
    {
        var idx = message.IndexOf("\n\n", StringComparison.Ordinal);
        return idx >= 0
            ? (message[..idx], message[(idx + 2)..])
            : (message, null);
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

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int TaskDialog(
        IntPtr hwndParent,
        IntPtr hInstance,
        [MarshalAs(UnmanagedType.LPWStr)] string pszWindowTitle,
        [MarshalAs(UnmanagedType.LPWStr)] string pszMainInstruction,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszContent,
        int dwCommonButtons,
        IntPtr pszIcon,
        out int pnButton);

    private const int TDCBF_OK_BUTTON = 0x0001;

    // The Win32 TD_* icon constants are MAKEINTRESOURCEW(-N), i.e. a 16-bit
    // truncation of a negative int into the low word of a pointer. Passing
    // `new IntPtr(-N)` sign-extends to 0xFFFFFFFFFFFFFFFN on 64-bit, which
    // TaskDialog's IS_INTRESOURCE check rejects as a pointer (because the
    // high word is non-zero) — it then tries to dereference it as a string
    // and AVs. Use the 16-bit truncated values directly.
    private static readonly IntPtr TD_ERROR_ICON = new(0xFFFE);       // MAKEINTRESOURCE(-2)
    private static readonly IntPtr TD_INFORMATION_ICON = new(0xFFFD); // MAKEINTRESOURCE(-3)
}
