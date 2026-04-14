using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLink.Core.Models;
using PowerLink.Core.Native;

namespace PowerLink.Core.Clone;

public record CloneResult
{
    public required int DirectoriesCreated { get; init; }
    public required int FilesLinked { get; init; }
    public required int FilesFailed { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public required bool DryRun { get; init; }
    public required string EffectiveDestPath { get; init; }
}

public class CloneEngine
{
    private readonly ILogger<CloneEngine> _logger;

    public CloneEngine(ILogger<CloneEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<CloneEngine>.Instance;
    }

    public async Task<CloneResult> CloneAsync(
        string sourcePath,
        string destPath,
        bool dryRun = false,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        var sourceFull = Path.GetFullPath(sourcePath);
        var destFull = Path.GetFullPath(destPath);

        if (!Directory.Exists(sourceFull))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceFull}");

        // If the destination already exists as a directory, treat it as the
        // PARENT and create a subdirectory named after the source folder
        // (cp -r / xcopy default behavior). If the destination doesn't exist,
        // it IS the target and gets created as-is.
        if (Directory.Exists(destFull))
        {
            var sourceName = Path.GetFileName(sourceFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(sourceName))
                destFull = Path.Combine(destFull, sourceName);
        }

        if (!Win32Hardlink.AreSameVolume(sourceFull, destFull))
            throw new InvalidOperationException(
                $"Source and destination must be on the same volume. " +
                $"Source='{sourceFull}', Dest='{destFull}'.");

        if (IsSubPath(sourceFull, destFull) || IsSubPath(destFull, sourceFull))
            throw new InvalidOperationException(
                "Destination cannot be inside source or vice versa.");

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var directoriesCreated = 0;
        var filesLinked = 0;
        var failures = new List<string>();

        await Task.Run(() =>
        {
            if (!dryRun)
                Directory.CreateDirectory(destFull);
            directoriesCreated++;

            foreach (var srcDir in Directory.EnumerateDirectories(sourceFull, "*", enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceFull, srcDir);
                var destDir = Path.Combine(destFull, relative);

                if (!dryRun)
                {
                    Directory.CreateDirectory(destDir);
                }
                directoriesCreated++;
            }

            var files = Directory.EnumerateFiles(sourceFull, "*", enumerationOptions).ToList();
            var processed = 0;

            foreach (var srcFile in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceFull, srcFile);
                var destFile = Path.Combine(destFull, relative);

                try
                {
                    if (!dryRun)
                    {
                        var parent = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrEmpty(parent))
                            Directory.CreateDirectory(parent);

                        Win32Hardlink.CreateHardLink(destFile, srcFile);
                    }
                    filesLinked++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Failed to link: {Src} -> {Dest}", srcFile, destFile);
                    failures.Add($"{srcFile}: {ex.Message}");
                }

                processed++;
                if (processed % 50 == 0)
                {
                    progress?.Report(new ScanProgress
                    {
                        Phase = ScanPhase.Executing,
                        FilesProcessed = processed,
                        TotalFiles = files.Count,
                        CurrentFile = srcFile,
                    });
                }
            }
        }, cancellationToken);

        return new CloneResult
        {
            DirectoriesCreated = directoriesCreated,
            FilesLinked = filesLinked,
            FilesFailed = failures.Count,
            Failures = failures,
            DryRun = dryRun,
            EffectiveDestPath = destFull,
        };
    }

    private static bool IsSubPath(string parent, string child)
    {
        var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var childFull = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(parentFull, childFull, StringComparison.OrdinalIgnoreCase))
            return true;
        return childFull.StartsWith(parentFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
