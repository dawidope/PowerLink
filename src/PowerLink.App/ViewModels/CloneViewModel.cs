using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLink.App.Services;
using PowerLink.Core.Clone;
using PowerLink.Core.Models;

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
    private string? _sourcePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _destPath;

    [ObservableProperty] private bool _dryRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _statusText = "Pick a source and destination.";
    [ObservableProperty] private string? _summaryText;
    [ObservableProperty] private string? _phaseText;
    [ObservableProperty] private string? _filesText;
    [ObservableProperty] private string? _speedText;
    [ObservableProperty] private string? _etaText;
    [ObservableProperty] private string? _currentFileText;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;

    [RelayCommand]
    private async Task PickSourceAsync()
    {
        var path = await FolderPickerService.PickFolderAsync();
        if (path is not null) SourcePath = path;
    }

    [RelayCommand]
    private async Task PickDestAsync()
    {
        var path = await FolderPickerService.PickFolderAsync();
        if (path is not null) DestPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(DestPath)) return;

        IsRunning = true;
        StatusText = DryRun ? "Dry run..." : "Cloning...";
        SummaryText = null;
        ResetProgress(indeterminate: true);
        _phaseStopwatch.Restart();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnProgress);

        try
        {
            var result = await _engine.CloneAsync(SourcePath, DestPath, DryRun, _cts.Token, progress);
            SummaryText =
                $"{(result.DryRun ? "[DRY RUN] " : string.Empty)}" +
                $"Cloned to: {result.EffectiveDestPath}\n" +
                $"Directories: {result.DirectoriesCreated:N0}, " +
                $"Files linked: {result.FilesLinked:N0}, Failed: {result.FilesFailed:N0}.";
            StatusText = result.FilesFailed == 0 ? "Done." : "Completed with failures.";
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally
        {
            IsRunning = false;
            _phaseStopwatch.Stop();
            ClearProgress();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelOp))]
    private void Cancel() => _cts?.Cancel();

    private bool CanRun() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(SourcePath) &&
        !string.IsNullOrWhiteSpace(DestPath);

    private bool CanCancelOp() => IsRunning;

    private void OnProgress(ScanProgress p)
    {
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
