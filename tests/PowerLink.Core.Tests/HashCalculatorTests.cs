using PowerLink.Core.Scanning;
using PowerLink.Core.Tests.TestHelpers;

namespace PowerLink.Core.Tests;

[Trait("Category", "Integration")]
public class HashCalculatorTests
{
    [Fact]
    public async Task ComputeHash_IdenticalFiles_ProduceSameHash()
    {
        using var temp = new TempDirectory();
        var content = new byte[1024];
        new Random(42).NextBytes(content);

        var a = temp.CreateFile("a.bin", content);
        var b = temp.CreateFile("b.bin", content);

        var hashA = await HashCalculator.ComputeHashAsync(a);
        var hashB = await HashCalculator.ComputeHashAsync(b);

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public async Task ComputeHash_DifferentContent_ProducesDifferentHash()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateFile("a.bin", "hello");
        var b = temp.CreateFile("b.bin", "world");

        var hashA = await HashCalculator.ComputeHashAsync(a);
        var hashB = await HashCalculator.ComputeHashAsync(b);

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public async Task ComputeHash_IsDeterministic()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("x.bin", "deterministic");

        var h1 = await HashCalculator.ComputeHashAsync(path);
        var h2 = await HashCalculator.ComputeHashAsync(path);

        Assert.Equal(h1, h2);
    }

    [Fact]
    public async Task ComputeHash_EmptyFile_ReturnsStableHash()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateFile("empty1.bin", Array.Empty<byte>());
        var b = temp.CreateFile("empty2.bin", Array.Empty<byte>());

        var h1 = await HashCalculator.ComputeHashAsync(a);
        var h2 = await HashCalculator.ComputeHashAsync(b);

        Assert.Equal(h1, h2);
    }

    [Fact]
    public async Task ComputeHash_LargeFile_Works()
    {
        using var temp = new TempDirectory();
        var content = new byte[256 * 1024];
        new Random(1).NextBytes(content);
        var path = temp.CreateFile("big.bin", content);

        var hash = await HashCalculator.ComputeHashAsync(path);
        Assert.False(string.IsNullOrWhiteSpace(hash));
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public async Task PrefixHash_IdenticalPrefixes_MatchEvenIfTailDiffers()
    {
        using var temp = new TempDirectory();
        var prefix = new byte[4096];
        new Random(99).NextBytes(prefix);

        var tailA = prefix.Concat(new byte[] { 0xAA, 0xBB }).ToArray();
        var tailB = prefix.Concat(new byte[] { 0xCC, 0xDD }).ToArray();
        var a = temp.CreateFile("a.bin", tailA);
        var b = temp.CreateFile("b.bin", tailB);

        var prefA = await HashCalculator.ComputePrefixHashAsync(a);
        var prefB = await HashCalculator.ComputePrefixHashAsync(b);
        Assert.Equal(prefA, prefB);

        var fullA = await HashCalculator.ComputeHashAsync(a);
        var fullB = await HashCalculator.ComputeHashAsync(b);
        Assert.NotEqual(fullA, fullB);
    }
}
