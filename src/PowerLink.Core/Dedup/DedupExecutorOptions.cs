namespace PowerLink.Core.Dedup;

public sealed record DedupExecutorOptions
{
    // When true, every action re-hashes both canonical and duplicate before
    // deleting, regardless of whether mtime/size/identity look unchanged.
    // Default is false: tiered verify trusts the scan-time hash when the
    // cheap stat checks all match.
    public bool AlwaysVerifyContent { get; init; }
}
