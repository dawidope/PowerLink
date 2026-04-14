using System.CommandLine;
using PowerLink.Core.Clone;
using PowerLink.Core.Dedup;
using PowerLink.Core.Models;
using PowerLink.Core.Scanning;

namespace PowerLink.Cli;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var root = new RootCommand("PowerLink — NTFS hardlink deduplication and directory cloning.");
        root.Add(BuildScanCommand());
        root.Add(BuildDedupCommand());
        root.Add(BuildCloneCommand());
        return root.Parse(args).InvokeAsync();
    }

    private static Command BuildScanCommand()
    {
        var pathsArg = new Argument<string[]>("paths")
        {
            Description = "One or more directories to scan.",
            Arity = ArgumentArity.OneOrMore,
        };
        var minSizeOpt = new Option<long>("--min-size", "-m")
        {
            Description = "Minimum file size in bytes (default 1 MiB).",
            DefaultValueFactory = _ => 1L * 1024 * 1024,
        };

        var cmd = new Command("scan", "Scan directories and report duplicate files.");
        cmd.Add(pathsArg);
        cmd.Add(minSizeOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var paths = parseResult.GetValue(pathsArg) ?? Array.Empty<string>();
            var minSize = parseResult.GetValue(minSizeOpt);
            return await ScanAsync(paths, minSize, ct);
        });

        return cmd;
    }

    private static Command BuildDedupCommand()
    {
        var pathsArg = new Argument<string[]>("paths")
        {
            Description = "One or more directories to deduplicate.",
            Arity = ArgumentArity.OneOrMore,
        };
        var minSizeOpt = new Option<long>("--min-size", "-m")
        {
            Description = "Minimum file size in bytes (default 1 MiB).",
            DefaultValueFactory = _ => 1L * 1024 * 1024,
        };
        var executeOpt = new Option<bool>("--execute")
        {
            Description = "Actually perform the deduplication (default is dry-run).",
        };

        var cmd = new Command("dedup", "Scan and replace duplicates with NTFS hardlinks.");
        cmd.Add(pathsArg);
        cmd.Add(minSizeOpt);
        cmd.Add(executeOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var paths = parseResult.GetValue(pathsArg) ?? Array.Empty<string>();
            var minSize = parseResult.GetValue(minSizeOpt);
            var execute = parseResult.GetValue(executeOpt);
            return await DedupAsync(paths, minSize, execute, ct);
        });

        return cmd;
    }

    private static Command BuildCloneCommand()
    {
        var sourceArg = new Argument<string>("source") { Description = "Source directory." };
        var destArg = new Argument<string>("dest") { Description = "Destination directory (same volume)." };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Report what would be cloned, don't touch the filesystem." };

        var cmd = new Command("clone", "Clone a directory tree by creating hardlinks instead of copies.");
        cmd.Add(sourceArg);
        cmd.Add(destArg);
        cmd.Add(dryRunOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceArg)!;
            var dest = parseResult.GetValue(destArg)!;
            var dryRun = parseResult.GetValue(dryRunOpt);
            return await CloneAsync(source, dest, dryRun, ct);
        });

        return cmd;
    }

    private static async Task<int> ScanAsync(string[] paths, long minSize, CancellationToken ct)
    {
        Console.WriteLine($"Scanning {paths.Length} location(s) (min size {FormatBytes(minSize)})...");

        var scanner = new FileScanner();
        var progress = new ConsoleProgress();

        var records = await scanner.ScanAsync(paths, minSize, ct, progress);
        Console.WriteLine();
        Console.WriteLine($"Found {records.Count:N0} files, total {FormatBytes(records.Sum(r => r.SizeBytes))}.");

        var engine = new DedupEngine();
        var result = await engine.AnalyzeAsync(records, ct, progress);
        Console.WriteLine();

        PrintReport(result);
        return 0;
    }

    private static async Task<int> DedupAsync(string[] paths, long minSize, bool execute, CancellationToken ct)
    {
        Console.WriteLine($"Scanning {paths.Length} location(s) (min size {FormatBytes(minSize)})...");

        var scanner = new FileScanner();
        var progress = new ConsoleProgress();

        var records = await scanner.ScanAsync(paths, minSize, ct, progress);
        Console.WriteLine();

        var engine = new DedupEngine();
        var result = await engine.AnalyzeAsync(records, ct, progress);
        Console.WriteLine();

        PrintReport(result);

        var plan = DedupEngine.CreatePlan(result);
        if (plan.ActionCount == 0)
        {
            Console.WriteLine("Nothing to do.");
            return 0;
        }

        if (!execute)
        {
            Console.WriteLine();
            Console.WriteLine($"DRY RUN — {plan.ActionCount:N0} actions would recover {FormatBytes(plan.TotalBytesToRecover)}.");
            Console.WriteLine("Re-run with --execute to apply.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"Executing {plan.ActionCount:N0} actions...");
        var executor = new DedupExecutor();
        var execResult = await executor.ExecuteAsync(plan, ct, progress);
        Console.WriteLine();
        Console.WriteLine($"Done. Success: {execResult.SuccessCount:N0}, Failures: {execResult.FailureCount:N0}, Recovered: {FormatBytes(execResult.BytesRecovered)}.");
        foreach (var failure in execResult.Failures.Take(20))
            Console.WriteLine($"  FAIL {failure.DuplicatePath}: {failure.Reason}");

        return execResult.FailureCount == 0 ? 0 : 1;
    }

    private static async Task<int> CloneAsync(string source, string dest, bool dryRun, CancellationToken ct)
    {
        Console.WriteLine($"{(dryRun ? "[DRY RUN] " : string.Empty)}Cloning '{source}' -> '{dest}'...");

        var engine = new CloneEngine();
        var progress = new ConsoleProgress();
        var result = await engine.CloneAsync(source, dest, dryRun, ct, progress);
        Console.WriteLine();
        Console.WriteLine($"Cloned to: {result.EffectiveDestPath}");
        Console.WriteLine($"Directories: {result.DirectoriesCreated:N0}, Files linked: {result.FilesLinked:N0}, Failed: {result.FilesFailed:N0}.");
        foreach (var f in result.Failures.Take(20))
            Console.WriteLine($"  FAIL {f}");
        return result.FilesFailed == 0 ? 0 : 1;
    }

    private static void PrintReport(ScanResult result)
    {
        Console.WriteLine($"Scanned {result.TotalFilesScanned:N0} files in {result.ScanDuration.TotalSeconds:F1}s.");
        Console.WriteLine($"Duplicate groups: {result.Groups.Count:N0}");
        Console.WriteLine($"Duplicate files:  {result.TotalDuplicates:N0}");
        Console.WriteLine($"Recoverable:      {FormatBytes(result.TotalWastedBytes)}");

        if (result.Groups.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine("Top duplicate groups:");
        foreach (var group in result.Groups.Take(20))
        {
            var dupCount = group.Duplicates.Count();
            Console.WriteLine($"  [{FormatBytes(group.FileSize)} \u00d7 {group.Files.Count}] saves {FormatBytes(group.WastedBytes)}");
            Console.WriteLine($"    keep:  {group.Canonical.FullPath}");
            foreach (var dup in group.Duplicates.Take(5))
                Console.WriteLine($"    dup:   {dup.FullPath}");
            if (dupCount > 5)
                Console.WriteLine($"    ... and {dupCount - 5:N0} more");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F2} {units[unit]}";
    }

    private sealed class ConsoleProgress : IProgress<ScanProgress>
    {
        private ScanPhase _lastPhase = (ScanPhase)(-1);
        private readonly bool _isInteractive = !Console.IsOutputRedirected;

        public void Report(ScanProgress value)
        {
            if (value.Phase != _lastPhase)
            {
                if ((int)_lastPhase >= 0) Console.WriteLine();
                _lastPhase = value.Phase;
            }

            var current = value.CurrentFile is null ? string.Empty : $" {Truncate(value.CurrentFile, 60)}";
            var suffix = value.TotalFiles > 0
                ? $"{value.Phase}: {value.FilesProcessed:N0} / {value.TotalFiles:N0}{current}"
                : $"{value.Phase}: {value.FilesProcessed:N0}{current}";

            if (_isInteractive)
            {
                var width = TryGetWindowWidth();
                var padded = suffix.Length > width ? suffix[..width] : suffix.PadRight(width);
                Console.Write("\r" + padded);
            }
            else
            {
                Console.WriteLine(suffix);
            }
        }

        private static int TryGetWindowWidth()
        {
            try
            {
                var w = Console.WindowWidth;
                return w > 0 ? w - 1 : 120;
            }
            catch (IOException)
            {
                return 120;
            }
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : "..." + s[^(max - 3)..];
    }
}
