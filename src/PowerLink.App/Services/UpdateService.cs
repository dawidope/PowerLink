using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace PowerLink.App.Services;

// GithubSource doubles as a plain release-feed reader even when the app
// isn't installed via Velopack, so portable users can call CheckAsync too
// — the fork only happens at apply time (Velopack vs browser).
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/dawidope/PowerLink";

    private readonly UpdateManager _manager;

    public UpdateService()
    {
        var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(source);
    }

    public bool IsVelopackInstalled => _manager.IsInstalled;

    public string CurrentVersionText
    {
        get
        {
            // UpdateManager.CurrentVersion is null when not installed via
            // Velopack. Fall back to assembly version so the Settings page
            // still has something to show in portable mode.
            var v = _manager.CurrentVersion;
            if (v != null) return v.ToString();
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public string ReleasesPageUrl => $"{RepoUrl}/releases/latest";

    // No CancellationToken: Velopack's CheckForUpdatesAsync / DownloadUpdatesAsync
    // don't accept one, so we'd be lying about cancellation.
    public async Task<UpdateCheckResult> CheckAsync()
    {
        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
                return new UpdateCheckResult(UpdateAvailability.UpToDate, null, null);

            var version = info.TargetFullRelease.Version.ToString();
            return new UpdateCheckResult(UpdateAvailability.Available, version, info);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateAvailability.Failed, null, null, ex.Message);
        }
    }

    public async Task ApplyVelopackAsync(UpdateInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!IsVelopackInstalled)
            throw new InvalidOperationException(
                "ApplyVelopackAsync requires the app to be installed via Velopack.");

        await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(info);
    }

    public void OpenReleasesPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ReleasesPageUrl,
            UseShellExecute = true,
        });
    }
}

public enum UpdateAvailability
{
    UpToDate,
    Available,
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    string? AvailableVersion,
    UpdateInfo? VelopackInfo,
    string? ErrorMessage = null);
