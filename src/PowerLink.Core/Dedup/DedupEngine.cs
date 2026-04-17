using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLink.Core.Models;
using PowerLink.Core.Scanning;

namespace PowerLink.Core.Dedup;

public class DedupEngine
{
    private readonly ILogger<DedupEngine> _logger;

    public DedupEngine(ILogger<DedupEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<DedupEngine>.Instance;
    }

    public async Task<ScanResult> AnalyzeAsync(
        IReadOnlyList<FileRecord> records,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null,
        int hashBufferSize = HashCalculator.DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfLessThan(hashBufferSize, 1024);

        var stopwatch = Stopwatch.StartNew();
        var totalBytes = records.Sum(r => r.SizeBytes);
        var wasCancelled = false;

        // Step 1: collapse entries that point to the same physical file (same volume + fileIndex).
        var physicallyUnique = records
            .GroupBy(r => (r.VolumeSerialNumber, r.FileIndex))
            .Select(g => g.First())
            .ToList();

        _logger.LogDebug(
            "Scanned {Count} records, {Unique} physically unique",
            records.Count, physicallyUnique.Count);

        // Step 2: group by (volume, size).
        var sizeCandidates = physicallyUnique
            .GroupBy(r => (r.VolumeSerialNumber, r.SizeBytes))
            .Where(g => g.Count() > 1)
            .ToList();

        // Step 3: prefix hash.
        var prefixCandidates = new List<FileRecord>();
        long prefixProcessed = 0;
        var totalPrefixCandidates = sizeCandidates.Sum(g => g.Count());

        try
        {
            foreach (var group in sizeCandidates)
            {
                foreach (var record in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        record.PrefixHash = await HashCalculator.ComputePrefixHashAsync(
                            record.FullPath, cancellationToken: cancellationToken);
                        prefixCandidates.Add(record);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Skipping unreadable file during prefix hash: {Path}", record.FullPath);
                    }

                    prefixProcessed++;
                    if (prefixProcessed % 25 == 0)
                    {
                        progress?.Report(new ScanProgress
                        {
                            Phase = ScanPhase.PrefixHashing,
                            FilesProcessed = prefixProcessed,
                            TotalFiles = totalPrefixCandidates,
                            CurrentFile = record.FullPath,
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }

        // Step 4: group by (volume, size, prefixHash).
        var fullHashCandidates = prefixCandidates
            .GroupBy(r => (r.VolumeSerialNumber, r.SizeBytes, r.PrefixHash))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        // Step 5: full hash.
        const long subFileProgressThreshold = 16L * 1024 * 1024;
        const long subFileReportInterval = 1L * 1024 * 1024;

        var totalBytesToHash = fullHashCandidates.Sum(r => r.SizeBytes);
        long fullProcessed = 0;
        long bytesAccumulated = 0;

        if (!wasCancelled)
        {
            try
            {
                foreach (var record in fullHashCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    IProgress<long>? subFileProgress = null;
                    if (record.SizeBytes >= subFileProgressThreshold)
                    {
                        long lastReported = 0;
                        var capturedAccumulated = bytesAccumulated;
                        var capturedProcessed = fullProcessed;
                        subFileProgress = new Progress<long>(bytesInFile =>
                        {
                            if (bytesInFile - lastReported < subFileReportInterval) return;
                            lastReported = bytesInFile;
                            progress?.Report(new ScanProgress
                            {
                                Phase = ScanPhase.FullHashing,
                                FilesProcessed = capturedProcessed,
                                TotalFiles = fullHashCandidates.Count,
                                BytesProcessed = capturedAccumulated + bytesInFile,
                                TotalBytes = totalBytesToHash,
                                CurrentFile = record.FullPath,
                            });
                        });
                    }

                    try
                    {
                        record.Hash = await HashCalculator.ComputeHashAsync(
                            record.FullPath, cancellationToken, subFileProgress, hashBufferSize);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Skipping unreadable file during full hash: {Path}", record.FullPath);
                    }

                    bytesAccumulated += record.SizeBytes;
                    fullProcessed++;
                    progress?.Report(new ScanProgress
                    {
                        Phase = ScanPhase.FullHashing,
                        FilesProcessed = fullProcessed,
                        TotalFiles = fullHashCandidates.Count,
                        BytesProcessed = bytesAccumulated,
                        TotalBytes = totalBytesToHash,
                        CurrentFile = record.FullPath,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }
        }

        // Step 6: form duplicate groups from whatever hashes we got.
        var groups = fullHashCandidates
            .Where(r => r.Hash is not null)
            .GroupBy(r => (r.VolumeSerialNumber, r.SizeBytes, r.Hash!))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup
            {
                Hash = g.Key.Item3,
                FileSize = g.Key.SizeBytes,
                Files = g.OrderBy(r => r.FileIndex).ToList(),
            })
            .OrderByDescending(g => g.WastedBytes)
            .ToList();

        stopwatch.Stop();

        return new ScanResult
        {
            Groups = groups,
            TotalFilesScanned = records.Count,
            TotalBytesScanned = totalBytes,
            ScanDuration = stopwatch.Elapsed,
            WasCancelled = wasCancelled,
        };
    }

    public static DedupPlan CreatePlan(ScanResult scanResult)
    {
        ArgumentNullException.ThrowIfNull(scanResult);

        var actions = new List<DedupAction>();
        foreach (var group in scanResult.Groups)
        {
            var canonical = group.Canonical;
            foreach (var duplicate in group.Duplicates)
            {
                actions.Add(new DedupAction
                {
                    DuplicatePath = duplicate.FullPath,
                    CanonicalPath = canonical.FullPath,
                    SizeBytes = group.FileSize,
                    Hash = group.Hash,
                    CanonicalSnapshot = FileSnapshot.From(canonical),
                    DuplicateSnapshot = FileSnapshot.From(duplicate),
                });
            }
        }

        return new DedupPlan { Actions = actions };
    }
}
