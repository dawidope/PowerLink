using CommunityToolkit.Mvvm.ComponentModel;
using PowerLink.Core.Models;

namespace PowerLink.App.ViewModels;

public partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroup Group { get; }

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Group = group;
        DuplicatePaths = group.Duplicates.Select(d => d.FullPath).ToList();
    }

    public string CanonicalPath => Group.Canonical.FullPath;
    public IReadOnlyList<string> DuplicatePaths { get; }
    public long FileSize => Group.FileSize;
    public long WastedBytes => Group.WastedBytes;
    public int FileCount => Group.Files.Count;
    public string Header => $"{FileCount} files \u2014 keep: {System.IO.Path.GetFileName(CanonicalPath)}";
}
