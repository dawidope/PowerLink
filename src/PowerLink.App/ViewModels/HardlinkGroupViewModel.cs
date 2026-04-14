using PowerLink.Core.Models;

namespace PowerLink.App.ViewModels;

public record HardlinkGroupViewModel
{
    public required long FileSize { get; init; }
    public required uint TotalHardLinkCount { get; init; }
    public required IReadOnlyList<string> PathsInScan { get; init; }

    public int LinksInScan => PathsInScan.Count;
    public int LinksOutsideScan => Math.Max(0, (int)TotalHardLinkCount - LinksInScan);
    public long SavedBytes => FileSize * (TotalHardLinkCount - 1);

    public string Header =>
        LinksOutsideScan == 0
            ? $"{TotalHardLinkCount} links"
            : $"{TotalHardLinkCount} links total ({LinksInScan} here, {LinksOutsideScan} elsewhere)";

    public static HardlinkGroupViewModel FromRecords(IReadOnlyList<FileRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) throw new ArgumentException("At least one record required.", nameof(records));

        return new HardlinkGroupViewModel
        {
            FileSize = records[0].SizeBytes,
            TotalHardLinkCount = records[0].HardLinkCount,
            PathsInScan = records.Select(r => r.FullPath).ToList(),
        };
    }
}
