using CommunityToolkit.Mvvm.ComponentModel;
using PowerLink.App.Services;

namespace PowerLink.App.ViewModels;

public partial class ShellVerbViewModel : ObservableObject
{
    public required ShellVerb Verb { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsInstalled { get; set; }

    // What the checkbox is currently bound to. Equals IsInstalled after
    // Refresh(); Apply() reconciles registry state to this value.
    [ObservableProperty]
    public partial bool ShouldInstall { get; set; }

    public string Label => Verb.Label;
    public string Description => Verb.Description;
    public string TargetsText => Verb.TargetsText;
    public string StatusText => (IsInstalled, Verb.RequiresElevation) switch
    {
        (true, _) => "Installed",
        (false, true) => "Needs admin",
        (false, false) => "Not installed",
    };
}
