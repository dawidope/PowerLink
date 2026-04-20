using PowerLink.Core.Native;
using PowerLink.Core.Scanning;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class JunctionScannerTests
{
    [Fact]
    public async Task Scan_EmptyTree_ReturnsNoJunctions()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt", "x");
        temp.CreateSubDirectory("sub");

        var scanner = new JunctionScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path });

        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_FindsJunctionsAtRootAndNested()
    {
        using var temp = new TempDirectory();
        var targetA = temp.CreateSubDirectory("targetA");
        var targetB = temp.CreateSubDirectory("targetB");
        var nested = temp.CreateSubDirectory("deeper/inner");
        Win32Junction.Create(Path.Combine(temp.Path, "link-root"), targetA);
        Win32Junction.Create(Path.Combine(nested, "link-nested"), targetB);

        var scanner = new JunctionScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, j => j.LinkPath.EndsWith("link-root", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, j => j.LinkPath.EndsWith("link-nested", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scan_DoesNotRecurseIntoJunction()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        File.WriteAllText(Path.Combine(target, "inside.txt"), "x");
        var targetSub = Path.Combine(target, "nested-target");
        Directory.CreateDirectory(targetSub);
        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, target);
        // If the scanner followed the junction, it would find a "nested-target"
        // entry inside the junction and might report it as a separate junction
        // or walk into it. It must not.
        Win32Junction.Create(Path.Combine(target, "bogus-junction"), target); // cycle trap

        var scanner = new JunctionScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path });

        // Two junctions in `target` (including the cycle one) plus one in root.
        // If we followed `link` we would walk into target, then through the
        // cycle junction, etc. The scanner must only find the link at root
        // plus the cycle junction at target/bogus-junction — not duplicates.
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Scan_FindsDanglingJunction()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubDirectory("target");
        var link = Path.Combine(temp.Path, "link");
        Win32Junction.Create(link, target);
        Directory.Delete(target);

        var scanner = new JunctionScanner();
        var result = await scanner.ScanAsync(new[] { temp.Path });

        var item = Assert.Single(result);
        Assert.True(item.IsTargetMissing);
    }
}
