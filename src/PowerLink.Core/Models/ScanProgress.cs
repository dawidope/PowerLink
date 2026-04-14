namespace PowerLink.Core.Models;

public enum ScanPhase
{
    Enumerating,
    PrefixHashing,
    FullHashing,
    Planning,
    Executing,
}

public record ScanProgress
{
    public required ScanPhase Phase { get; init; }
    public required long FilesProcessed { get; init; }
    public required long TotalFiles { get; init; }
    public long BytesProcessed { get; init; }
    public long TotalBytes { get; init; }
    public string? CurrentFile { get; init; }
}
