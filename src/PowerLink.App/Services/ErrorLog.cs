namespace PowerLink.App.Services;

// Append-only error log next to Velopack's install root, so crashes leave a
// managed stack trace instead of only a WER entry (which for stowed WinRT
// exceptions carries no .NET stack at all).
public static class ErrorLog
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerLink", "error.log");

    private static readonly object _lock = new();

    public static void Write(string source, Exception? ex, string? note = null)
    {
        try
        {
            var line = $"[{DateTime.UtcNow:O}] {source} pid={Environment.ProcessId} " +
                       $"elevated={ElevationService.IsElevated}" +
                       (note is null ? "" : $" note={note}") +
                       Environment.NewLine +
                       (ex?.ToString() ?? "(no exception object)") +
                       Environment.NewLine + Environment.NewLine;

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Logging must never be the thing that takes the app down.
        }
    }
}
