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

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
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

                foreach (var path in Directory.EnumerateFiles(root, "*", enumerationOptions))
                {
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
