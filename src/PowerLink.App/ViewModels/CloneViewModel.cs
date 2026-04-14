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
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnProgress);

        try
        {
            var result = await _engine.CloneAsync(SourcePath, DestPath, DryRun, _cts.Token, progress);
            SummaryText =
                $"{(result.DryRun ? "[DRY RUN] " : string.Empty)}" +
                $"Directories: {result.DirectoriesCreated:N0}, " +
                $"Files linked: {result.FilesLinked:N0}, Failed: {result.FilesFailed:N0}.";
            StatusText = result.FilesFailed == 0 ? "Done." : "Completed with failures.";
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally
        {
            IsRunning = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            PhaseText = null;
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
}
