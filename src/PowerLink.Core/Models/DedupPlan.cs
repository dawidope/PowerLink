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
    public bool WasCancelled { get; init; }

    // Actions whose duplicate already pointed to the canonical's physical file
    // (e.g. someone hardlinked them between scan and apply). Not a failure;
    // also not counted under SuccessCount / BytesRecovered because no work was
    // done and no bytes were freed.
    public int AlreadyLinkedCount { get; init; }
}

public record DedupFailure
{
    public required string DuplicatePath { get; init; }
    public required string CanonicalPath { get; init; }
    public required string Reason { get; init; }
}
