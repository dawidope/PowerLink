namespace PowerLink.Core.Models;

public record FileRecord
{
    public required string FullPath { get; init; }
    public required long SizeBytes { get; init; }
    public string? Hash { get; set; }
    public string? PrefixHash { get; set; }
    public required uint HardLinkCount { get; init; }
    public required ulong FileIndex { get; init; }
    public required uint VolumeSerialNumber { get; init; }
}
