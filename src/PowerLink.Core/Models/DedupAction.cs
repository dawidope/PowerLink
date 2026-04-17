namespace PowerLink.Core.Models;

public record DedupAction
{
    public required string DuplicatePath { get; init; }
    public required string CanonicalPath { get; init; }
    public required long SizeBytes { get; init; }
    public required string Hash { get; init; }

    // Identity + freshness fingerprints captured at scan time. The executor
    // uses these for tiered verification (identity + optional content re-hash)
    // before deleting the duplicate.
    public required FileSnapshot CanonicalSnapshot { get; init; }
    public required FileSnapshot DuplicateSnapshot { get; init; }
}

// Identity + freshness fingerprint of a file, captured during scan.
// VolumeSerialNumber + FileIndex uniquely identify the physical NTFS file.
// LastWriteTimeUtc lets the executor cheaply detect content changes between
// scan and apply without re-hashing on the happy path.
public readonly record struct FileSnapshot(
    uint VolumeSerialNumber,
    ulong FileIndex,
    long SizeBytes,
    DateTime LastWriteTimeUtc)
{
    public static FileSnapshot From(FileRecord record) => new(
        record.VolumeSerialNumber,
        record.FileIndex,
        record.SizeBytes,
        record.LastWriteTimeUtc);
}
