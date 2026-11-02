namespace PowerLink.Core.Launch;

public static class PathInput
{
    // Cleans up a path the user typed or pasted. Accepts what Explorer's
    // "Copy as path" produces (wrapped in quotes) and rejects relative paths,
    // which would silently resolve against the process's current directory.
    // Returns null when the input isn't a usable absolute path.
    public static string? TryNormalize(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim().Trim('"').Trim();
        if (s.Length == 0 || !Path.IsPathFullyQualified(s)) return null;
        try
        {
            return Path.GetFullPath(s);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
