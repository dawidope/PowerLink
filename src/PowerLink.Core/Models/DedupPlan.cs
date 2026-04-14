namespace PowerLink.Core.Models;

public record DedupPlan
{
    public required IReadOnlyList<DedupAction> Actions { get; init; }
    public long TotalBytesToRecover => Actions.Sum(a => a.SizeBytes);
    public int ActionCount => Actions.Count;
}

public record DedupExecutionResult
{
    public required int SuccessCount { get; init; }
    public required int FailureCount { get; init; }
    public required long BytesRecovered { get; init; }
    public required IReadOnlyList<DedupFailure> Failures { get; init; }
}

public record DedupFailure
{
    public required string DuplicatePath { get; init; }
    public required string CanonicalPath { get; init; }
    public required string Reason { get; init; }
}
