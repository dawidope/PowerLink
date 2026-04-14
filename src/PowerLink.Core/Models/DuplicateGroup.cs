namespace PowerLink.Core.Models;

public record DuplicateGroup
{
    public required string Hash { get; init; }
    public required long FileSize { get; init; }
    public required IReadOnlyList<FileRecord> Files { get; init; }

    public FileRecord Canonical => Files[0];
    public IEnumerable<FileRecord> Duplicates => Files.Skip(1);
    public long WastedBytes => FileSize * (Files.Count - 1);
}
