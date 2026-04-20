using PowerLink.Core.Native;

namespace PowerLink.App.ViewModels;

public record JunctionViewModel
{
    public required string LinkPath { get; init; }
    public required string TargetPath { get; init; }
    public required bool IsTargetMissing { get; init; }

    public string StatusText => IsTargetMissing
        ? "dangling — target does not exist"
        : "OK";

    public string Arrow => IsTargetMissing ? "\u26A0 \u2192" : "\u2192";

    public static JunctionViewModel FromInfo(JunctionInfo info) => new()
    {
        LinkPath = info.LinkPath,
        TargetPath = info.TargetPath,
        IsTargetMissing = info.IsTargetMissing,
    };
}
