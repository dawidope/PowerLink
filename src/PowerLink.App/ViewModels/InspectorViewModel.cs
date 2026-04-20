using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLink.App.Services;
using PowerLink.Core.Models;
using PowerLink.Core.Native;
using PowerLink.Core.Scanning;

namespace PowerLink.App.ViewModels;

public partial class InspectorViewModel : ObservableObject
{
    private readonly FileScanner _scanner = new();
    private readonly JunctionScanner _junctionScanner = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Paths { get; } = new();
    public ObservableCollection<HardlinkGroupViewModel> Groups { get; } = new();
    public ObservableCollection<JunctionViewModel> Junctions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string? SummaryText { get; set; }
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsProgressIndeterminate { get; set; }
    [ObservableProperty] public partial string? CurrentFileText { get; set; }
    [ObservableProperty] public partial string? FilesText { get; set; }

    public string HardlinksHeader => $"Hardlinks ({Groups.Count:N0})";
    public string JunctionsHeader
    {
        get
        {
            var dangling = Junctions.Count(j => j.IsTargetMissing);
            var suffix = dangling > 0 ? $", {dangling:N0} dangling" : string.Empty;
            return $"Junctions ({Junctions.Count:N0}{suffix})";
        }
    }

    private void NotifyHeaderChanged()
    {
        OnPropertyChanged(nameof(HardlinksHeader));
        OnPropertyChanged(nameof(JunctionsHeader));
    }

    public InspectorViewModel()
    {
        StatusText = "Add folders to inspect.";
        Paths.CollectionChanged += (_, _) => ScanCommand.NotifyCanExecuteChanged();
        Groups.CollectionChanged += (_, _) => NotifyHeaderChanged();
        Junctions.CollectionChanged += (_, _) => NotifyHeaderChanged();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = await PickerService.PickFolderAsync();
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
        if (Paths.Count == 0) return;

        IsScanning = true;
        Groups.Clear();
        Junctions.Clear();
        SummaryText = null;
        StatusText = "Scanning...";
        IsProgressIndeterminate = true;
        ProgressValue = 0;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnProgress);
        var sw = Stopwatch.StartNew();

        try
        {
            var records = await _scanner.ScanAsync(Paths, minSizeBytes: 0, _cts.Token, progress);

            // Group by (volume, fileIndex) — same underlying physical file.
            // HardLinkCount from MFT tells us total links across the whole
            // volume; records show only those that live inside the scanned
            // folders, so LinksOutsideScan = total - in-scan.
            var groups = records
                .Where(r => r.HardLinkCount > 1)
                .GroupBy(r => (r.VolumeSerialNumber, r.FileIndex))
                .Select(g => HardlinkGroupViewModel.FromRecords(g.ToList()))
                .OrderByDescending(g => g.SavedBytes)
                .ToList();

            foreach (var g in groups) Groups.Add(g);

            var junctions = await _junctionScanner.ScanAsync(Paths, _cts.Token);
            foreach (var j in junctions.OrderBy(j => j.LinkPath, StringComparer.OrdinalIgnoreCase))
                Junctions.Add(JunctionViewModel.FromInfo(j));

            var totalSaved = groups.Sum(g => g.SavedBytes);
            var totalLinks = groups.Sum(g => g.LinksInScan);
            var dangling = Junctions.Count(j => j.IsTargetMissing);

            sw.Stop();
            SummaryText =
                $"Scanned {records.Count:N0} files across {Paths.Count} location(s) in {sw.Elapsed.TotalSeconds:F1}s. " +
                $"Found {groups.Count:N0} hardlinked file{(groups.Count == 1 ? string.Empty : "s")} " +
                $"({totalLinks:N0} link{(totalLinks == 1 ? string.Empty : "s")} in scan), " +
                $"{FormatBytes(totalSaved)} shared. " +
                $"Junctions: {Junctions.Count:N0}" +
                (dangling > 0 ? $" ({dangling:N0} dangling)" : string.Empty) + ".";
            StatusText = groups.Count == 0 && Junctions.Count == 0
                ? "No hardlinks or junctions found in these folders."
                : "Scan complete.";
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

    [RelayCommand]
    private void DeleteJunction(JunctionViewModel? jvm)
    {
        if (jvm is null) return;
        try
        {
            Win32Junction.Delete(jvm.LinkPath);
            Junctions.Remove(jvm);
            StatusText = $"Deleted junction: {jvm.LinkPath} (target preserved)";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RepairJunctionAsync(JunctionViewModel? jvm)
    {
        if (jvm is null) return;
        var newTarget = await PickerService.PickFolderAsync();
        if (newTarget is null) return;

        try
        {
            Win32Junction.Repair(jvm.LinkPath, newTarget);
            var updated = Win32Junction.Read(jvm.LinkPath)!;
            var idx = Junctions.IndexOf(jvm);
            if (idx >= 0)
                Junctions[idx] = JunctionViewModel.FromInfo(updated);
            StatusText = $"Repaired junction: {jvm.LinkPath} \u2192 {newTarget}";
        }
        catch (Exception ex)
        {
            StatusText = $"Repair failed: {ex.Message}";
        }
    }

    private bool CanScan() => !IsScanning && Paths.Count > 0;
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
