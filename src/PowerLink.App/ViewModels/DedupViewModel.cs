using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLink.App.Services;
using PowerLink.Core.Dedup;
using PowerLink.Core.Models;
using PowerLink.Core.Scanning;

namespace PowerLink.App.ViewModels;

public partial class DedupViewModel : ObservableObject
{
    private readonly FileScanner _scanner = new();
    private readonly DedupEngine _engine = new();
    private readonly DedupExecutor _executor = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Paths { get; } = new();
    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    [ObservableProperty] private double _minSizeMiB = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteDedupCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteDedupCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isExecuting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteDedupCommand))]
    private DedupPlan? _plan;

    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private string? _summaryText;
    [ObservableProperty] private string? _phaseText;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;

    public DedupViewModel()
    {
        Paths.CollectionChanged += (_, _) => ScanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = await FolderPickerService.PickFolderAsync();
        if (path is not null && !Paths.Contains(path))
            Paths.Add(path);
    }

    [RelayCommand]
    private void RemoveFolder(string? path)
    {
        if (path is not null && Paths.Contains(path))
            Paths.Remove(path);
    }

    [RelayCommand]
    private void ClearFolders() => Paths.Clear();

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        Groups.Clear();
        Plan = null;
        SummaryText = null;
        StatusText = "Scanning...";
        IsProgressIndeterminate = true;
        ProgressValue = 0;

        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnProgress);

        try
        {
            var minBytes = (long)(MinSizeMiB * 1024 * 1024);
            var records = await _scanner.ScanAsync(Paths, minBytes, _cts.Token, progress);
            var result = await _engine.AnalyzeAsync(records, _cts.Token, progress);

            foreach (var g in result.Groups)
                Groups.Add(new DuplicateGroupViewModel(g));

            Plan = DedupEngine.CreatePlan(result);
            SummaryText =
                $"Scanned {result.TotalFilesScanned:N0} files in {result.ScanDuration.TotalSeconds:F1}s. " +
                $"Found {result.Groups.Count:N0} groups, {result.TotalDuplicates:N0} duplicates, " +
                $"recoverable: {FormatBytes(result.TotalWastedBytes)}.";
            StatusText = result.Groups.Count == 0 ? "No duplicates found." : "Scan complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            PhaseText = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteDedupAsync()
    {
        if (Plan is null || Plan.ActionCount == 0) return;

        IsExecuting = true;
        StatusText = $"Executing {Plan.ActionCount:N0} actions...";
        IsProgressIndeterminate = false;
        ProgressValue = 0;

        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnProgress);

        try
        {
            var result = await _executor.ExecuteAsync(Plan, _cts.Token, progress);
            SummaryText =
                $"Done. Success: {result.SuccessCount:N0}, Failures: {result.FailureCount:N0}, " +
                $"Recovered: {FormatBytes(result.BytesRecovered)}.";
            StatusText = result.FailureCount == 0 ? "Deduplication complete." : "Completed with failures.";
            Plan = null;
            Groups.Clear();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsExecuting = false;
            ProgressValue = 0;
            PhaseText = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelOp))]
    private void Cancel() => _cts?.Cancel();

    private bool CanScan() => Paths.Count > 0 && !IsScanning && !IsExecuting;
    private bool CanExecute() => Plan is { ActionCount: > 0 } && !IsScanning && !IsExecuting;
    private bool CanCancelOp() => IsScanning || IsExecuting;

    private void OnProgress(ScanProgress p)
    {
        PhaseText = p.TotalFiles > 0
            ? $"{p.Phase}: {p.FilesProcessed:N0} / {p.TotalFiles:N0}"
            : $"{p.Phase}: {p.FilesProcessed:N0}";

        if (p.TotalFiles > 0)
        {
            IsProgressIndeterminate = false;
            ProgressValue = (double)p.FilesProcessed / p.TotalFiles * 100.0;
        }
        else
        {
            IsProgressIndeterminate = true;
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
}
