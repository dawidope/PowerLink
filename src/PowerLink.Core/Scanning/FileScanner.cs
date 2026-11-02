using System.IO.Enumeration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLink.Core.Models;
using PowerLink.Core.Native;

namespace PowerLink.Core.Scanning;

public class FileScanner
{
    private readonly ILogger<FileScanner> _logger;

    public FileScanner(ILogger<FileScanner>? logger = null)
    {
        _logger = logger ?? NullLogger<FileScanner>.Instance;
    }

    // Folders the last ScanAsync call couldn't read because of missing
    // permissions (ERROR_ACCESS_DENIED). They're skipped, so the results are
    // incomplete — the UI uses this to offer "Restart as administrator".
    public int InaccessibleDirectoryCount { get; private set; }

    public async Task<IReadOnlyList<FileRecord>> ScanAsync(
        IEnumerable<string> roots,
        long minSizeBytes = 0,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegative(minSizeBytes);

        var records = new List<FileRecord>();
        long processed = 0;
        InaccessibleDirectoryCount = 0;

        // IgnoreInaccessible stays off: AccessDeniedCountingEnumerator skips
        // those errors itself so it can count them.
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        await Task.Run(() =>
        {
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(root))
                {
                    _logger.LogWarning("Skipping missing directory: {Root}", root);
                    continue;
                }

                using var files = new AccessDeniedCountingEnumerator(root, enumerationOptions);
                while (files.MoveNext())
                {
                    var path = files.Current;
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var info = Win32Hardlink.GetFileInformation(path);
                        if (info.SizeBytes < minSizeBytes)
                            continue;

                        records.Add(new FileRecord
                        {
                            FullPath = path,
                            SizeBytes = info.SizeBytes,
                            HardLinkCount = info.HardLinkCount,
                            FileIndex = info.FileIndex,
                            VolumeSerialNumber = info.VolumeSerialNumber,
                            LastWriteTimeUtc = info.LastWriteTimeUtc,
                        });

                        processed++;
                        if (processed % 100 == 0)
                        {
                            progress?.Report(new ScanProgress
                            {
                                Phase = ScanPhase.Enumerating,
                                FilesProcessed = processed,
                                TotalFiles = processed,
                                CurrentFile = path,
                            });
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Skipping unreadable file: {Path}", path);
                    }
                }

                InaccessibleDirectoryCount += files.AccessDeniedCount;
            }
        }, cancellationToken);

        progress?.Report(new ScanProgress
        {
            Phase = ScanPhase.Enumerating,
            FilesProcessed = processed,
            TotalFiles = processed,
        });

        return records;
    }
}

// Same as Directory.EnumerateFiles(root, "*", options), except that it
// counts directories it had to skip because of ERROR_ACCESS_DENIED instead
// of dropping them silently.
internal sealed class AccessDeniedCountingEnumerator : FileSystemEnumerator<string>
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    public AccessDeniedCountingEnumerator(string directory, EnumerationOptions options)
        : base(directory, options)
    {
    }

    public int AccessDeniedCount { get; private set; }

    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) => !entry.IsDirectory;

    protected override string TransformEntry(ref FileSystemEntry entry) => entry.ToFullPath();

    // Called for directories that fail to open. Matches what
    // IgnoreInaccessible=true skipped, plus a count of the access-denied ones.
    protected override bool ContinueOnError(int error)
    {
        if (error == ErrorAccessDenied)
        {
            AccessDeniedCount++;
            return true;
        }
        return error == ErrorSharingViolation;
    }
}
