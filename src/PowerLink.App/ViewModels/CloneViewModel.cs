using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLink.App.Services;
using PowerLink.Core.Clone;
using PowerLink.Core.Models;
using PowerLink.Core.Native;

namespace PowerLink.App.ViewModels;

public partial class CloneViewModel : ObservableObject
{
    private readonly CloneEngine _engine = new();
    private CancellationTokenSource? _cts;

    private readonly Stopwatch _phaseStopwatch = new();
    private DateTime _lastUiFlush = DateTime.MinValue;
    private const int UiUpdateIntervalMs = 100;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? SourcePath { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? DestPath { get; set; }

    [ObservableProperty] public partial bool DryRun { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string? SummaryText { get; set; }
    [ObservableProperty] public partial string? PhaseText { get; set; }
    [ObservableProperty] public partial string? FilesText { get; set; }
    [ObservableProperty] public partial string? SpeedText { get; set; }
    [ObservableProperty] public partial string? EtaText { get; set; }
    [ObservableProperty] public partial string? CurrentFileText { get; set; }
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsProgressIndeterminate { get; set; }

    public CloneViewModel()
    {
        StatusText = "Pick a source and destination.";
    }

    [RelayCommand]
    private async Task PickSourceFolderAsync()
    {
        var path = await PickerService.PickFolderAsync();
        if (path is not null) SourcePath = path;
    }

    [RelayCommand]
    private async Task PickSourceFileAsync()
    {
        var path = await PickerService.PickFileAsync();
        if (path is not null) SourcePath = path;
    }

    [RelayCommand]
    private async Task PickDestAsync()
    {
        var path = await PickerService.PickFolderAsync();
        if (path is not null) DestPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(DestPath)) return;

        IsRunning = true;
        SummaryText = null;
        ResetProgress(indeterminate: true);
        _phaseStopwatch.Restart();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var sourceFull = Path.GetFullPath(SourcePath);
            var destFull = Path.GetFullPath(DestPath);

            if (File.Exists(sourceFull))
            {
                StatusText = DryRun ? "Dry run (file)..." : "Hardlinking file...";
                await LinkSingleFileAsync(sourceFull, destFull);
            }
            else if (Directory.Exists(sourceFull))
            {
                StatusText = DryRun ? "Dry run (folder)..." : "Cloning folder...";
                var progress = new Progress<ScanProgress>(OnProgress);
                var result = await _engine.CloneAsync(sourceFull, destFull, DryRun, _cts.Token, progress);
                SummaryText =
                    $"{(result.DryRun ? "[DRY RUN] " : string.Empty)}" +
                    $"Cloned to: {result.EffectiveDestPath}\n" +
                    $"Directories: {result.DirectoriesCreated:N0}, " +
                    $"Files linked: {result.FilesLinked:N0}, Failed: {result.FilesFailed:N0}.";
                StatusText = result.FilesFailed == 0 ? "Done." : "Completed with failures.";
            }
            else
            {
                StatusText = $"Source not found: {sourceFull}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            (StatusText, SummaryText) = FormatFriendlyError(ex);
        }
        finally
        {
            IsRunning = false;
            _phaseStopwatch.Stop();
            ClearProgress();
        }
    }

    private async Task LinkSingleFileAsync(string sourceFile, string destDir)
    {
        var ct = _cts!.Token;
        var dryRun = DryRun;
        var linkPath = Path.Combine(destDir, Path.GetFileName(sourceFile));

        // Friendly pre-checks done on UI thread so we can short-circuit
        // before the background work and surface a readable message.
        if (string.Equals(linkPath, sourceFile, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Source and destination are the same.";
            SummaryText = "Pick a different destination folder than the file's own folder.";
            return;
        }
        if (File.Exists(linkPath))
        {
            StatusText = "A file with that name already exists in the destination.";
            SummaryText = $"Cannot overwrite: {linkPath}";
            return;
        }
        if (!Win32Hardlink.AreSameVolume(sourceFile, destDir))
        {
            StatusText = "Source and destination are on different volumes.";
            SummaryText = "Hardlinks only work within a single NTFS volume.";
            return;
        }

        // All filesystem work runs on a background thread; UI properties are
        // assigned only after we're back on the dispatcher to avoid
        // RPC_E_WRONG_THREAD on the x:Bind setter.
        var destExisted = await Task.Run(() =>
        {
            var existed = Directory.Exists(destDir);
            if (!existed && !dryRun)
                Directory.CreateDirectory(destDir);
            if (!dryRun)
                Win32Hardlink.CreateHardLink(linkPath, sourceFile);
            return existed;
        }, ct);

        SummaryText = dryRun
            ? (destExisted
                ? $"[DRY RUN] Would hardlink: {linkPath} -> {sourceFile}"
                : $"[DRY RUN] Would create {destDir} then hardlink {Path.GetFileName(sourceFile)} into it")
            : $"Hardlinked: {linkPath} -> {sourceFile}";

        StatusText = "Done.";
    }

    private static (string Status, string Summary) FormatFriendlyError(Exception ex) => ex switch
    {
        IOException io when io.HResult == unchecked((int)0x800700B7) || io.Message.Contains("Win32 error 183")
            => ("Target already exists.", "A file or folder with that name already exists in the destination."),
        IOException io when io.HResult == unchecked((int)0x80070005) || io.Message.Contains("Win32 error 5")
            => ("Access denied.", "Windows refused the operation. Check folder permissions or that the file isn't open in another program."),
        IOException io when io.HResult == unchecked((int)0x80070020) || io.Message.Contains("Win32 error 32")
            => ("File is in use.", "Another process is holding the source or target file open. Close it and retry."),
        UnauthorizedAccessException
            => ("Access denied.", ex.Message),
        InvalidOperationException
            => ("Operation not allowed.", ex.Message),
        DirectoryNotFoundException
            => ("Not found.", ex.Message),
        _ => ($"Error: {ex.GetType().Name}", ex.Message),
    };

    [RelayCommand(CanExecute = nameof(CanCancelOp))]
    private void Cancel() => _cts?.Cancel();

    private bool CanRun() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(SourcePath) &&
        !string.IsNullOrWhiteSpace(DestPath);

    private bool CanCancelOp() => IsRunning;

    private void OnProgress(ScanProgress p)
    {
        // Progress<T>.Report posts via SynchronizationContext — a late
        // callback can land after the finally block cleared UI state.
        if (!IsRunning) return;

        var isFinal = p.TotalFiles > 0 && p.FilesProcessed >= p.TotalFiles;
        var now = DateTime.UtcNow;
        if (!isFinal && (now - _lastUiFlush).TotalMilliseconds < UiUpdateIntervalMs)
            return;
        _lastUiFlush = now;

        PhaseText = "Linking files";
        FilesText = p.TotalFiles > 0
            ? $"{p.FilesProcessed:N0} / {p.TotalFiles:N0} files"
            : $"{p.FilesProcessed:N0} files";
        CurrentFileText = string.IsNullOrEmpty(p.CurrentFile) ? null : Path.GetFileName(p.CurrentFile);

        var elapsedSec = _phaseStopwatch.Elapsed.TotalSeconds;
        if (p.TotalFiles > 0)
        {
            if (elapsedSec >= 0.5 && p.FilesProcessed > 0)
            {
                var rate = p.FilesProcessed / elapsedSec;
                SpeedText = $"{rate:F0} files/s";
                var remaining = p.TotalFiles - p.FilesProcessed;
                EtaText = rate > 0 ? $"ETA {FormatDuration(remaining / rate)}" : null;
            }
            IsProgressIndeterminate = false;
            ProgressValue = (double)p.FilesProcessed / p.TotalFiles * 100.0;
        }
        else
        {
            IsProgressIndeterminate = true;
        }
    }

    private void ResetProgress(bool indeterminate)
    {
        _lastUiFlush = DateTime.MinValue;
        IsProgressIndeterminate = indeterminate;
        ProgressValue = 0;
        PhaseText = null;
        FilesText = null;
        SpeedText = null;
        EtaText = null;
        CurrentFileText = null;
    }

    private void ClearProgress()
    {
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        PhaseText = null;
        FilesText = null;
        SpeedText = null;
        EtaText = null;
        CurrentFileText = null;
    }

    private static string FormatDuration(double seconds)
    {
        if (double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds < 0) return "?";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
