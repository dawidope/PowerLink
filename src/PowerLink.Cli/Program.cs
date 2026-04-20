using System.CommandLine;
using PowerLink.Core.Clone;
using PowerLink.Core.Dedup;
using PowerLink.Core.Models;
using PowerLink.Core.Native;
using PowerLink.Core.Scanning;
using PowerLink.Core.State;

namespace PowerLink.Cli;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var root = new RootCommand("PowerLink — NTFS hardlink deduplication and directory cloning.");
        root.Add(BuildScanCommand());
        root.Add(BuildDedupCommand());
        root.Add(BuildCloneCommand());
        root.Add(BuildPickCommand());
        root.Add(BuildDropCommand());
        root.Add(BuildShowLinksCommand());
        root.Add(BuildInstallOverlayCommand());
        root.Add(BuildUninstallOverlayCommand());
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
        var verifyContentOpt = new Option<bool>("--verify-content")
        {
            Description = "Re-hash both files before deleting (paranoid mode). Default is mtime-triggered re-hash only.",
        };

        var cmd = new Command("dedup", "Scan and replace duplicates with NTFS hardlinks.");
        cmd.Add(pathsArg);
        cmd.Add(minSizeOpt);
        cmd.Add(executeOpt);
        cmd.Add(verifyContentOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var paths = parseResult.GetValue(pathsArg) ?? Array.Empty<string>();
            var minSize = parseResult.GetValue(minSizeOpt);
            var execute = parseResult.GetValue(executeOpt);
            var verifyContent = parseResult.GetValue(verifyContentOpt);
            return await DedupAsync(paths, minSize, execute, verifyContent, ct);
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

    private static Command BuildPickCommand()
    {
        var pathArg = new Argument<string>("path") { Description = "File or folder to remember as the link source." };
        var cmd = new Command("pick", "Remember a path as the link source for a later 'drop'.");
        cmd.Add(pathArg);
        cmd.SetAction((parseResult, _) => Task.FromResult(Pick(parseResult.GetValue(pathArg)!)));
        return cmd;
    }

    private static Command BuildDropCommand()
    {
        var targetArg = new Argument<string>("target") { Description = "Target directory where the hardlink (or cloned tree) is created." };
        var cmd = new Command("drop", "Create a hardlink (file) or cloned hardlink tree (directory) at <target> using the previously picked source.");
        cmd.Add(targetArg);
        cmd.SetAction((parseResult, ct) => DropAsync(parseResult.GetValue(targetArg)!, ct));
        return cmd;
    }

    private static Command BuildShowLinksCommand()
    {
        var pathArg = new Argument<string>("path") { Description = "File to list hardlinks for." };
        var cmd = new Command("show-links", "List every path that shares this file's data on disk.");
        cmd.Add(pathArg);
        cmd.SetAction((parseResult, _) => Task.FromResult(ShowLinks(parseResult.GetValue(pathArg)!)));
        return cmd;
    }

    private static Command BuildInstallOverlayCommand()
    {
        var dllArg = new Argument<string>("dll-path")
        {
            Description = "Path to PowerLink.ShellExt.dll.",
        };
        var cmd = new Command("install-overlay",
            "Register the hardlink overlay handler in HKLM. Requires admin.");
        cmd.Add(dllArg);
        cmd.SetAction((parseResult, _) => Task.FromResult(InstallOverlay(parseResult.GetValue(dllArg)!)));
        return cmd;
    }

    private static Command BuildUninstallOverlayCommand()
    {
        var cmd = new Command("uninstall-overlay",
            "Unregister the hardlink overlay handler from HKLM. Requires admin.");
        cmd.SetAction((_, _) => Task.FromResult(UninstallOverlay()));
        return cmd;
    }

    private static int InstallOverlay(string dllPath)
    {
        if (!OverlayInstaller.IsElevated())
        {
            Console.Error.WriteLine("install-overlay must run as administrator.");
            return 2;
        }
        try
        {
            var fullPath = Path.GetFullPath(dllPath);
            OverlayInstaller.Install(fullPath);
            Console.WriteLine($"Registered overlay handler: {fullPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"install-overlay failed: {ex.Message}");
            return 1;
        }
    }

    private static int UninstallOverlay()
    {
        if (!OverlayInstaller.IsElevated())
        {
            Console.Error.WriteLine("uninstall-overlay must run as administrator.");
            return 2;
        }
        try
        {
            OverlayInstaller.Uninstall();
            Console.WriteLine("Unregistered overlay handler.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"uninstall-overlay failed: {ex.Message}");
            return 1;
        }
    }

    private static int ShowLinks(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            ShellUi.ReportError("PowerLink: Show hardlinks", $"File not found: {full}");
            return 1;
        }
        try
        {
            var links = Win32Hardlink.EnumerateHardLinks(full);
            var name = Path.GetFileName(full);
            if (links.Count <= 1)
            {
                ShellUi.ReportInfo("PowerLink: Hardlinks",
                    $"'{name}' has no hardlinks — it's the only on-disk reference to this data.");
                return 0;
            }
            var body = $"'{name}' shares its data with {links.Count - 1} other path(s):\n\n" +
                       string.Join(Environment.NewLine, links);
            ShellUi.ReportInfo("PowerLink: Hardlinks", body);
            return 0;
        }
        catch (Exception ex)
        {
            ShellUi.ReportError("PowerLink: Show hardlinks", ex.Message);
            return 1;
        }
    }

    private static int Pick(string path)
    {
        var full = Path.GetFullPath(path);
        var isDir = Directory.Exists(full);
        var isFile = File.Exists(full);

        if (!isDir && !isFile)
        {
            ShellUi.ReportError("PowerLink: Pick failed", $"Path not found: {full}");
            return 1;
        }

        var picked = new PickedSource
        {
            Path = full,
            PickedAtUtc = DateTime.UtcNow,
            IsDirectory = isDir,
        };
        PickedSourceStore.Save(picked);
        Console.WriteLine($"Picked {(isDir ? "folder" : "file")}: {full}");
        return 0;
    }

    private static async Task<int> DropAsync(string target, CancellationToken ct)
    {
        var picked = PickedSourceStore.TryLoad();
        if (picked is null)
        {
            ShellUi.ReportError("PowerLink: Drop failed",
                "Nothing picked. Right-click a file or folder first and choose 'PowerLink: Pick as link source'.");
            return 2;
        }

        var targetFull = Path.GetFullPath(target);
        if (!Directory.Exists(targetFull))
        {
            ShellUi.ReportError("PowerLink: Drop failed", $"Target directory not found: {targetFull}");
            return 1;
        }

        if (!File.Exists(picked.Path) && !Directory.Exists(picked.Path))
        {
            ShellUi.ReportError("PowerLink: Drop failed", $"Picked source no longer exists: {picked.Path}");
            return 3;
        }

        try
        {
            if (picked.IsDirectory)
            {
                Console.WriteLine($"Cloning {picked.Path} -> {targetFull} as hardlinks...");
                var engine = new CloneEngine();
                var progress = new ConsoleProgress();
                var result = await engine.CloneAsync(picked.Path, targetFull, dryRun: false, ct, progress);
                Console.Error.WriteLine();
                Console.WriteLine($"Linked into: {result.EffectiveDestPath}");
                Console.WriteLine($"Directories: {result.DirectoriesCreated:N0}, Files linked: {result.FilesLinked:N0}, Failed: {result.FilesFailed:N0}.");
                return result.FilesFailed == 0 ? 0 : 1;
            }
            else
            {
                if (!Win32Hardlink.AreSameVolume(picked.Path, targetFull))
                {
                    ShellUi.ReportError("PowerLink: Drop failed",
                        "Source and target must be on the same NTFS volume. Hardlinks cannot cross volumes.");
                    return 1;
                }
                var linkPath = Path.Combine(targetFull, Path.GetFileName(picked.Path));
                if (File.Exists(linkPath))
                {
                    ShellUi.ReportError("PowerLink: Drop failed",
                        $"A file named '{Path.GetFileName(picked.Path)}' already exists in the target.");
                    return 1;
                }
                Win32Hardlink.CreateHardLink(linkPath, picked.Path);
                Console.WriteLine($"Hardlinked: {linkPath} -> {picked.Path}");
                return 0;
            }
        }
        catch (Exception ex)
        {
            ShellUi.ReportError("PowerLink: Drop failed", ex.Message);
            return 1;
        }
    }

    private static async Task<int> ScanAsync(string[] paths, long minSize, CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"Scanning {paths.Length} location(s) (min size {FormatBytes(minSize)})...");

            var scanner = new FileScanner();
            var progress = new ConsoleProgress();

            var records = await scanner.ScanAsync(paths, minSize, ct, progress);
            Console.Error.WriteLine();
            Console.WriteLine($"Found {records.Count:N0} files, total {FormatBytes(records.Sum(r => r.SizeBytes))}.");

            var engine = new DedupEngine();
            var result = await engine.AnalyzeAsync(records, ct, progress);
            Console.Error.WriteLine();

            PrintReport(result);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"scan failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DedupAsync(string[] paths, long minSize, bool execute, bool verifyContent, CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"Scanning {paths.Length} location(s) (min size {FormatBytes(minSize)})...");

            var scanner = new FileScanner();
            var progress = new ConsoleProgress();

            var records = await scanner.ScanAsync(paths, minSize, ct, progress);
            Console.Error.WriteLine();

            var engine = new DedupEngine();
            var result = await engine.AnalyzeAsync(records, ct, progress);
            Console.Error.WriteLine();

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
            Console.WriteLine($"Executing {plan.ActionCount:N0} actions{(verifyContent ? " (with content re-verify)" : string.Empty)}...");
            var executor = new DedupExecutor();
            var execResult = await executor.ExecuteAsync(
                plan, ct, progress, options: new DedupExecutorOptions { AlwaysVerifyContent = verifyContent });
            Console.Error.WriteLine();
            var prefix = execResult.WasCancelled ? "Stopped. Partial — " : "Done. ";
            var alreadyLinked = execResult.AlreadyLinkedCount > 0
                ? $", Already linked: {execResult.AlreadyLinkedCount:N0}"
                : string.Empty;
            Console.WriteLine($"{prefix}Success: {execResult.SuccessCount:N0}, Failures: {execResult.FailureCount:N0}{alreadyLinked}, Recovered: {FormatBytes(execResult.BytesRecovered)}.");
            foreach (var failure in execResult.Failures.Take(20))
                Console.WriteLine($"  FAIL {failure.DuplicatePath}: {failure.Reason}");

            return execResult.FailureCount == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dedup failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CloneAsync(string source, string dest, bool dryRun, CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"{(dryRun ? "[DRY RUN] " : string.Empty)}Cloning '{source}' -> '{dest}'...");

            var engine = new CloneEngine();
            var progress = new ConsoleProgress();
            var result = await engine.CloneAsync(source, dest, dryRun, ct, progress);
            Console.Error.WriteLine();
            Console.WriteLine($"Cloned to: {result.EffectiveDestPath}");
            Console.WriteLine($"Directories: {result.DirectoriesCreated:N0}, Files linked: {result.FilesLinked:N0}, Failed: {result.FilesFailed:N0}.");
            foreach (var f in result.Failures.Take(20))
                Console.WriteLine($"  FAIL {f}");
            return result.FilesFailed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"clone failed: {ex.Message}");
            return 1;
        }
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
        // Interactivity is gated on stderr (where progress goes), not stdout —
        // a user piping `dedup ... | tee log.txt` still wants the live spinner
        // on their terminal, not a flood of WriteLines polluting the log.
        private readonly bool _isInteractive = !Console.IsErrorRedirected;

        public void Report(ScanProgress value)
        {
            if (value.Phase != _lastPhase)
            {
                if ((int)_lastPhase >= 0) Console.Error.WriteLine();
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
                Console.Error.Write("\r" + padded);
            }
            else
            {
                Console.Error.WriteLine(suffix);
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
