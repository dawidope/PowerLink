using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLink.Core.Native;

namespace PowerLink.Core.Scanning;

public class JunctionScanner
{
    private readonly ILogger<JunctionScanner> _logger;

    public JunctionScanner(ILogger<JunctionScanner>? logger = null)
    {
        _logger = logger ?? NullLogger<JunctionScanner>.Instance;
    }

    public Task<IReadOnlyList<JunctionInfo>> ScanAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        return Task.Run<IReadOnlyList<JunctionInfo>>(() =>
        {
            var results = new List<JunctionInfo>();

            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(root))
                {
                    _logger.LogWarning("Skipping missing directory: {Root}", root);
                    continue;
                }

                WalkForJunctions(root, results, cancellationToken);
            }

            return results;
        }, cancellationToken);
    }

    // Manual recursion so we can detect reparse-point directories and capture
    // them instead of descending into them. EnumerationOptions with
    // AttributesToSkip=ReparsePoint would silently drop junctions, which is
    // the opposite of what we want here.
    private void WalkForJunctions(string dir, List<JunctionInfo> results, CancellationToken ct)
    {
        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Skipping unreadable directory: {Dir}", dir);
            return;
        }

        foreach (var sub in subdirs)
        {
            ct.ThrowIfCancellationRequested();

            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(sub);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Skipping inaccessible entry: {Entry}", sub);
                continue;
            }

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                try
                {
                    var info = Win32Junction.Read(sub);
                    if (info is not null)
                        results.Add(info);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to read reparse data: {Entry}", sub);
                }

                // Never descend into a reparse point: either it's a junction
                // (already captured) or it's a non-junction reparse point
                // (symlink, WSL mount, etc.) which we don't follow either.
                continue;
            }

            WalkForJunctions(sub, results, ct);
        }
    }
}
