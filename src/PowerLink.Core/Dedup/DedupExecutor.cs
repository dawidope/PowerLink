using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLink.Core.Models;
using PowerLink.Core.Native;

namespace PowerLink.Core.Dedup;

public class DedupExecutor
{
    private readonly ILogger<DedupExecutor> _logger;

    public DedupExecutor(ILogger<DedupExecutor>? logger = null)
    {
        _logger = logger ?? NullLogger<DedupExecutor>.Instance;
    }

    public async Task<DedupExecutionResult> ExecuteAsync(
        DedupPlan plan,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var failures = new List<DedupFailure>();
        var successCount = 0;
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
                    VerifyCanonical(action);

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

        await Task.CompletedTask;

        return new DedupExecutionResult
        {
            SuccessCount = successCount,
            FailureCount = failures.Count,
            BytesRecovered = bytesRecovered,
            Failures = failures,
            WasCancelled = wasCancelled,
        };
    }

    private static void VerifyCanonical(DedupAction action)
    {
        if (!File.Exists(action.CanonicalPath))
            throw new FileNotFoundException(
                $"Canonical file missing: {action.CanonicalPath}", action.CanonicalPath);

        var info = Win32Hardlink.GetFileInformation(action.CanonicalPath);
        if (info.SizeBytes != action.SizeBytes)
            throw new InvalidOperationException(
                $"Canonical file size mismatch at {action.CanonicalPath}: " +
                $"expected {action.SizeBytes}, found {info.SizeBytes}");
    }
}
