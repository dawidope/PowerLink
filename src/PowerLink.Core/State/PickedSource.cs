namespace PowerLink.Core.State;

public record PickedSource
{
    public required string Path { get; init; }
    public required DateTime PickedAtUtc { get; init; }
    public required bool IsDirectory { get; init; }
}
