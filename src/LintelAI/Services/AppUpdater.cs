using Velopack;
using Velopack.Sources;
using VelopackUpdateInfo = Velopack.UpdateInfo;

namespace LintelAI.Services;

/// <summary>A newer build that can be installed in place.</summary>
public sealed record InPlaceUpdate(Version Version, VelopackUpdateInfo Info);

/// <summary>
/// In-place updates through Velopack: it downloads only what changed, swaps the app and restarts it.
///
/// This only works for a copy installed by the Velopack setup. A copy from the older Inno Setup
/// installer, or one started with "dotnet run", is not "installed" as far as Velopack is concerned,
/// so <see cref="IsInstalled"/> is false and the caller falls back to the notify-only
/// <see cref="UpdateChecker"/>, which just points to the release page.
/// </summary>
public sealed class AppUpdater
{
    private UpdateManager? _manager;
    private string? _repository;

    /// <summary>Whether this copy of the app was put here by Velopack and can replace itself.</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                // The manager needs a source, but "installed" is a property of the running copy, not of it.
                return new UpdateManager("https://github.com").IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Returns null when this copy is already the newest.</summary>
    /// <param name="repository">owner/name of a public GitHub repository whose releases hold the packages.</param>
    public async Task<InPlaceUpdate?> CheckAsync(string repository)
    {
        // The repository comes from a settings file a person can edit, so it is checked, not trusted.
        if (!UpdateChecker.IsValidRepository(repository))
            throw new InvalidOperationException("The update repository should look like owner/name.");

        var url = $"https://github.com/{repository}";

        if (_manager is null || _repository != repository)
        {
            _manager = new UpdateManager(new GithubSource(url, accessToken: null, prerelease: false));
            _repository = repository;
        }

        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;

        return new InPlaceUpdate(ToVersion(info.TargetFullRelease.Version.ToString()), info);
    }

    /// <param name="progress">Percent, 0 to 100.</param>
    public Task DownloadAsync(InPlaceUpdate update, Action<int> progress, CancellationToken ct = default) =>
        (_manager ?? throw new InvalidOperationException("Check for updates first."))
            .DownloadUpdatesAsync(update.Info, progress, ct);

    /// <summary>Closes the app, swaps in the downloaded version and starts it again.</summary>
    public void ApplyAndRestart(InPlaceUpdate update) =>
        (_manager ?? throw new InvalidOperationException("Check for updates first."))
            .ApplyUpdatesAndRestart(update.Info.TargetFullRelease);

    private static Version ToVersion(string text) =>
        UpdateChecker.TryParseVersion(text, out var version) ? version : new Version(0, 0, 0);
}
