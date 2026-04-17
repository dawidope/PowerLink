using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLink.Core.Models;
using PowerLink.Core.Native;
using PowerLink.Core.Scanning;

namespace PowerLink.Core.Dedup;

public class DedupExecutor
{
    private readonly ILogger<DedupExecutor> _logger;

    public DedupExecutor(ILogger<DedupExecutor>? logger = null)
    {
        _logger = logger ?? NullLogger<DedupExecutor>.Instance;
    }

    public Task<DedupExecutionResult> ExecuteAsync(
        DedupPlan plan,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null,
        DedupExecutorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // File.Delete + CreateHardLink are blocking Win32 calls. Running the
        // loop inline on an async-over-sync method kept it pinned to the
        // caller's thread (the WinUI UI thread for DedupPage), freezing the
        // window for the duration of the plan. Task.Run moves the whole pass
        // to the thread pool so the UI thread stays responsive.
        return Task.Run(() => ExecutePlanAsync(
            plan, options ?? new DedupExecutorOptions(), cancellationToken, progress));
    }

    private async Task<DedupExecutionResult> ExecutePlanAsync(
        DedupPlan plan,
        DedupExecutorOptions options,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress)
    {
        var failures = new List<DedupFailure>();
        var successCount = 0;
        var alreadyLinkedCount = 0;
        long bytesRecovered = 0;
        var wasCancelled = false;

        try
        {
            for (var i = 0; i < plan.Actions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var action = plan.Actions[i];

                try
                {
                    var outcome = await VerifyActionAsync(action, options, cancellationToken)
                        .ConfigureAwait(false);

                    if (outcome == VerifyOutcome.AlreadyLinked)
                    {
                        alreadyLinkedCount++;
                        _logger.LogDebug(
                            "Skipping action: {Duplicate} already hardlinked to {Canonical}",
                            action.DuplicatePath, action.CanonicalPath);
                    }
                    else
                    {
                        File.Delete(action.DuplicatePath);
                        try
                        {
                            Win32Hardlink.CreateHardLink(action.DuplicatePath, action.CanonicalPath);
                            successCount++;
                            bytesRecovered += action.SizeBytes;
                        }
                        catch
                        {
                            _logger.LogError(
                                "Hardlink creation failed after deleting duplicate: {Duplicate}. " +
                                "Canonical is intact at {Canonical}.",
                                action.DuplicatePath, action.CanonicalPath);
                            throw;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dedup action failed: {Duplicate} -> {Canonical}",
                        action.DuplicatePath, action.CanonicalPath);
                    failures.Add(new DedupFailure
                    {
                        DuplicatePath = action.DuplicatePath,
                        CanonicalPath = action.CanonicalPath,
                        Reason = ex.Message,
                    });
                }

                progress?.Report(new ScanProgress
                {
                    Phase = ScanPhase.Executing,
                    FilesProcessed = i + 1,
                    TotalFiles = plan.Actions.Count,
                    BytesProcessed = bytesRecovered,
                    TotalBytes = plan.TotalBytesToRecover,
                    CurrentFile = action.DuplicatePath,
                });
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }

        return new DedupExecutionResult
        {
            SuccessCount = successCount,
            FailureCount = failures.Count,
            AlreadyLinkedCount = alreadyLinkedCount,
            BytesRecovered = bytesRecovered,
            Failures = failures,
            WasCancelled = wasCancelled,
        };
    }

    private enum VerifyOutcome { Proceed, AlreadyLinked }

    // Tiered verification before deleting a duplicate:
    //   1. Hard identity checks on both files (existence, size, volume serial,
    //      NTFS file index). Catches "file replaced under our path".
    //   2. Soft mtime check — if either file's mtime drifted from the scan
    //      snapshot, re-hash that file and compare against the recorded hash.
    //   3. AlwaysVerifyContent forces the re-hash regardless of mtime — opt-in
    //      paranoid mode for environments where mtime can't be trusted.
    // If the duplicate already shares its physical file with the canonical
    // (e.g. someone hardlinked them between scan and apply), the action is a
    // no-op rather than a delete-and-relink.
    private static async Task<VerifyOutcome> VerifyActionAsync(
        DedupAction action,
        DedupExecutorOptions options,
        CancellationToken cancellationToken)
    {
        var canonSnap = action.CanonicalSnapshot;
        var dupSnap = action.DuplicateSnapshot;

        if (!File.Exists(action.CanonicalPath))
            throw new FileNotFoundException(
                $"Canonical file missing: {action.CanonicalPath}", action.CanonicalPath);

        var canonInfo = Win32Hardlink.GetFileInformation(action.CanonicalPath);
        if (canonInfo.SizeBytes != action.SizeBytes)
            throw new InvalidOperationException(
                $"Canonical file size mismatch at {action.CanonicalPath}: " +
                $"expected {action.SizeBytes}, found {canonInfo.SizeBytes}");

        if (canonInfo.VolumeSerialNumber != canonSnap.VolumeSerialNumber)
            throw new InvalidOperationException(
                $"Canonical volume changed at {action.CanonicalPath}: " +
                $"expected serial {canonSnap.VolumeSerialNumber}, found {canonInfo.VolumeSerialNumber}");
        if (canonInfo.FileIndex != canonSnap.FileIndex)
            throw new InvalidOperationException(
                $"Canonical file replaced at {action.CanonicalPath}: NTFS file index changed");

        if (!File.Exists(action.DuplicatePath))
            throw new FileNotFoundException(
                $"Duplicate file missing: {action.DuplicatePath}", action.DuplicatePath);

        var dupInfo = Win32Hardlink.GetFileInformation(action.DuplicatePath);

        // Same physical file — desired end state already reached.
        if (dupInfo.VolumeSerialNumber == canonInfo.VolumeSerialNumber &&
            dupInfo.FileIndex == canonInfo.FileIndex)
            return VerifyOutcome.AlreadyLinked;

        if (dupInfo.SizeBytes != dupSnap.SizeBytes)
            throw new InvalidOperationException(
                $"Duplicate file size changed at {action.DuplicatePath}: " +
                $"expected {dupSnap.SizeBytes}, found {dupInfo.SizeBytes}");
        if (dupInfo.VolumeSerialNumber != dupSnap.VolumeSerialNumber)
            throw new InvalidOperationException(
                $"Duplicate volume changed at {action.DuplicatePath}: " +
                $"expected serial {dupSnap.VolumeSerialNumber}, found {dupInfo.VolumeSerialNumber}");
        if (dupInfo.FileIndex != dupSnap.FileIndex)
            throw new InvalidOperationException(
                $"Duplicate file replaced at {action.DuplicatePath}: NTFS file index changed");

        var canonMtimeChanged = canonInfo.LastWriteTimeUtc != canonSnap.LastWriteTimeUtc;
        var dupMtimeChanged = dupInfo.LastWriteTimeUtc != dupSnap.LastWriteTimeUtc;

        if (options.AlwaysVerifyContent || canonMtimeChanged)
        {
            var actualHash = await HashCalculator.ComputeHashAsync(
                action.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, action.Hash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Canonical content changed at {action.CanonicalPath}: " +
                    "hash no longer matches scan-time value");
        }

        if (options.AlwaysVerifyContent || dupMtimeChanged)
        {
            var actualHash = await HashCalculator.ComputeHashAsync(
                action.DuplicatePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, action.Hash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Duplicate content changed at {action.DuplicatePath}: " +
                    "hash no longer matches scan-time value");
        }

        return VerifyOutcome.Proceed;
    }
}
