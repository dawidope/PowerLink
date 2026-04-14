using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLink.App.Services;
using PowerLink.Core.Models;
using PowerLink.Core.Scanning;

namespace PowerLink.App.ViewModels;

public partial class InspectorViewModel : ObservableObject
{
    private readonly FileScanner _scanner = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<HardlinkGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private string? _folderPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isScanning;

    [ObservableProperty] private string _statusText = "Pick a folder to inspect.";
    [ObservableProperty] private string? _summaryText;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string? _currentFileText;
    [ObservableProperty] private string? _filesText;

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var path = await PickerService.PickFolderAsync();
        if (path is not null) FolderPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath)) return;

        IsScanning = true;
        Groups.Clear();
        SummaryText = null;
        StatusText = "Scanning...";
        IsProgressIndeterminate = true;
        ProgressValue = 0;

        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnProgress);
        var sw = Stopwatch.StartNew();

        try
        {
            var records = await _scanner.ScanAsync(new[] { FolderPath }, minSizeBytes: 0, _cts.Token, progress);

            // Group by (volume, fileIndex) — same underlying physical file.
            // Only groups with nLinks > 1 are interesting; a solo entry with
            // HardLinkCount == 1 is just a regular file. A solo entry with
            // HardLinkCount > 1 means siblings exist OUTSIDE the scanned folder.
            var groups = records
                .Where(r => r.HardLinkCount > 1)
                .GroupBy(r => (r.VolumeSerialNumber, r.FileIndex))
                .Select(g => HardlinkGroupViewModel.FromRecords(g.ToList()))
                .OrderByDescending(g => g.SavedBytes)
                .ToList();

            foreach (var g in groups) Groups.Add(g);

            var totalSaved = groups.Sum(g => g.SavedBytes);
            var totalLinks = groups.Sum(g => g.LinksInScan);

            sw.Stop();
            SummaryText =
                $"Scanned {records.Count:N0} files in {sw.Elapsed.TotalSeconds:F1}s. " +
                $"Found {groups.Count:N0} hardlinked file{(groups.Count == 1 ? string.Empty : "s")} " +
                $"({totalLinks:N0} link{(totalLinks == 1 ? string.Empty : "s")} in scan), " +
                $"{FormatBytes(totalSaved)} shared.";
            StatusText = groups.Count == 0 ? "No hardlinks found in this folder." : "Scan complete.";
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
            CurrentFileText = null;
            FilesText = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelOp))]
    private void Cancel() => _cts?.Cancel();

    private bool CanScan() => !IsScanning && !string.IsNullOrWhiteSpace(FolderPath);
    private bool CanCancelOp() => IsScanning;

    private void OnProgress(ScanProgress p)
    {
        // Progress<T>.Report posts via SynchronizationContext — the callback
        // may run AFTER ScanAsync's finally block has already reset UI state.
        // Guard so a late-arriving progress event doesn't re-start the spinner.
        if (!IsScanning) return;

        FilesText = $"{p.FilesProcessed:N0} files";
        CurrentFileText = string.IsNullOrEmpty(p.CurrentFile) ? null : Path.GetFileName(p.CurrentFile);
        IsProgressIndeterminate = true; // scanner reports count but no total
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
