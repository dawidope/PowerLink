namespace PowerLink.Core.Models;

public record ScanResult
{
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }
    public required long TotalFilesScanned { get; init; }
    public required long TotalBytesScanned { get; init; }
    public required TimeSpan ScanDuration { get; init; }

    public long TotalDuplicates => Groups.Sum(g => (long)g.Duplicates.Count());
    public long TotalWastedBytes => Groups.Sum(g => g.WastedBytes);
}
