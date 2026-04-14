using System.IO.Hashing;

namespace PowerLink.Core.Scanning;

public static class HashCalculator
{
    public const int DefaultBufferSize = 64 * 1024;
    public const int DefaultPrefixBytes = 4096;

    public static async Task<string> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken = default,
        IProgress<long>? bytesProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hasher = new XxHash128();
        var buffer = new byte[DefaultBufferSize];
        long totalRead = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;

            hasher.Append(buffer.AsSpan(0, read));
            totalRead += read;
            bytesProgress?.Report(totalRead);
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    public static async Task<string> ComputePrefixHashAsync(
        string path,
        int prefixBytes = DefaultPrefixBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(prefixBytes, 1);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            prefixBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hasher = new XxHash128();
        var buffer = new byte[prefixBytes];
        var totalRead = 0;

        while (totalRead < prefixBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, prefixBytes - totalRead), cancellationToken);
            if (read == 0) break;
            totalRead += read;
        }

        hasher.Append(buffer.AsSpan(0, totalRead));
        return Convert.ToHexString(hasher.GetHashAndReset());
    }
}
