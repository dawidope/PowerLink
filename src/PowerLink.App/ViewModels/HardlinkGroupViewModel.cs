using Microsoft.UI.Xaml;
using PowerLink.Core.Models;
using PowerLink.Core.Native;

namespace PowerLink.App.ViewModels;

public record HardlinkGroupViewModel
{
    public required long FileSize { get; init; }
    public required uint TotalHardLinkCount { get; init; }
    public required IReadOnlyList<string> PathsInScan { get; init; }
    public required IReadOnlyList<string> PathsElsewhere { get; init; }
    public required bool EnumerationFailed { get; init; }

    public int LinksInScan => PathsInScan.Count;

    // When enumeration succeeded we trust its result; when it failed
    // (rare — access denied, etc.) fall back to MFT-count arithmetic so
    // the user still sees "N elsewhere" even if we can't list them.
    public int LinksOutsideScan => EnumerationFailed
        ? Math.Max(0, (int)TotalHardLinkCount - LinksInScan)
        : PathsElsewhere.Count;

    public long SavedBytes => TotalHardLinkCount > 1
        ? FileSize * (long)(TotalHardLinkCount - 1)
        : 0;

    public string Header =>
        LinksOutsideScan == 0
            ? $"{TotalHardLinkCount} links"
            : $"{TotalHardLinkCount} links total ({LinksInScan} here, {LinksOutsideScan} elsewhere)";

    public Visibility ElsewhereListVisibility =>
        PathsElsewhere.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static HardlinkGroupViewModel FromRecords(IReadOnlyList<FileRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) throw new ArgumentException("At least one record required.", nameof(records));

        var pathsInScan = records.Select(r => r.FullPath).ToList();
        var pathsElsewhere = Array.Empty<string>() as IReadOnlyList<string>;
        var enumerationFailed = false;

        try
        {
            // MFT gives us every path on the volume that shares this file's
            // data. Subtract in-scan paths to get everything else.
            var allOnVolume = Win32Hardlink.EnumerateHardLinks(records[0].FullPath);
            var inScanSet = new HashSet<string>(pathsInScan, StringComparer.OrdinalIgnoreCase);
            pathsElsewhere = allOnVolume
                .Where(p => !inScanSet.Contains(p))
                .ToList();
        }
        catch (IOException)
        {
            enumerationFailed = true;
        }

        return new HardlinkGroupViewModel
        {
            FileSize = records[0].SizeBytes,
            TotalHardLinkCount = records[0].HardLinkCount,
            PathsInScan = pathsInScan,
            PathsElsewhere = pathsElsewhere,
            EnumerationFailed = enumerationFailed,
        };
    }
}
