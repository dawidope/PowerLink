namespace PowerLink.Core.Launch;

public enum LaunchMode { Dedup, Inspect, Clone, Junction }

// What a PowerLink.App launch should open: a page plus the paths to load
// into it. Built from `--<mode> <path>` pairs on the command line (shell
// verbs pass one, "Restart as administrator" passes the page's whole list).
public sealed record LaunchPreset(LaunchMode Mode, IReadOnlyList<string> Paths)
{
    public static string Flag(LaunchMode mode) => mode switch
    {
        LaunchMode.Dedup => "--dedup",
        LaunchMode.Inspect => "--inspect",
        LaunchMode.Clone => "--clone",
        LaunchMode.Junction => "--junction",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static LaunchMode? ModeFromFlag(string arg) => arg switch
    {
        "--dedup" => LaunchMode.Dedup,
        "--inspect" => LaunchMode.Inspect,
        "--clone" => LaunchMode.Clone,
        "--junction" => LaunchMode.Junction,
        _ => null,
    };

    // Parses command-line arguments (without the exe path). The first mode
    // flag decides the page; later pairs for a different mode are ignored.
    // A flag with no following path is ignored. Returns null when no flag.
    public static LaunchPreset? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        LaunchMode? mode = null;
        var paths = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            var current = ModeFromFlag(args[i]);
            if (current is null) continue;

            mode ??= current;
            if (current == mode)
                paths.Add(args[i + 1]);
            i++; // skip the path we just consumed
        }
        return mode is null ? null : new LaunchPreset(mode.Value, paths);
    }

    // Inverse of Parse: one `--<mode> <path>` pair per path, unquoted —
    // hand them to ProcessStartInfo.ArgumentList, which does the quoting.
    public IEnumerable<string> ToArguments() =>
        Paths.SelectMany(p => new[] { Flag(Mode), p });
}
