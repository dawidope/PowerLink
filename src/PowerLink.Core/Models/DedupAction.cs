namespace PowerLink.Core.Models;

public record DedupAction
{
    public required string DuplicatePath { get; init; }
    public required string CanonicalPath { get; init; }
    public required long SizeBytes { get; init; }
    public required string Hash { get; init; }
}
