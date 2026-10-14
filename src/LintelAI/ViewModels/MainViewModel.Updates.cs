using Avalonia.Threading;
using LintelAI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LintelAI.ViewModels;

/// <summary>
/// Updating in place. This only appears for a copy installed by the Velopack setup; otherwise the
/// banner stays what it always was, a notice with a link to the release page.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly AppUpdater _appUpdater = new();
    private InPlaceUpdate? _inPlaceUpdate;

    /// <summary>The banner offers "Update and restart" instead of only a link.</summary>
    [ObservableProperty] private bool _canUpdateInPlace;

    [ObservableProperty] private bool _isInstallingUpdate;

    private async Task CheckInPlaceAsync(bool manual)
    {
        try
        {
            var update = await _appUpdater.CheckAsync(Settings.UpdateRepository);

            if (update is null)
            {
                CanUpdateInPlace = false;
                if (manual) Notify($"You are on the latest version ({AppInfo.CurrentText}).", ToastKind.Success);
                return;
            }

            _inPlaceUpdate = update;

            // Reuse the notify-only plumbing so "Skip this version" and "View release" keep working.
            var tag = "v" + update.Version.ToString(3);
            _availableUpdate = new UpdateInfo(
                update.Version, tag, $"https://github.com/{Settings.UpdateRepository}/releases/tag/{tag}", "");

            var skipped = string.Equals(Settings.SkippedVersion, tag, StringComparison.OrdinalIgnoreCase);
            if (skipped && !manual) return;

            UpdateTitle = $"Version {update.Version.ToString(3)} is available";
            UpdateDetail = $"You are on {AppInfo.CurrentText}. Updating downloads only what changed, then restarts the app.";
            CanUpdateInPlace = true;
            IsUpdateBannerVisible = true;
            if (manual) Notify($"Version {update.Version.ToString(3)} is available.", ToastKind.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (manual) Notify($"Couldn't check for updates: {ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_inPlaceUpdate is null || IsInstallingUpdate) return;

        // The app closes and restarts; a review or scan in progress would be thrown away.
        if (IsAnalyzing)
        {
            Notify("Wait for the current run to finish, or stop it, before updating.", ToastKind.Error);
            return;
        }

        var update = _inPlaceUpdate;
        var before = UpdateDetail;
        IsInstallingUpdate = true;
        UpdateDetail = "Downloading…";

        try
        {
            await _appUpdater.DownloadAsync(
                update,
                percent => Dispatcher.UIThread.Post(() => UpdateDetail = $"Downloading… {percent}%"));

            UpdateDetail = "Restarting…";
            _appUpdater.ApplyAndRestart(update); // closes this process
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsInstallingUpdate = false;
            UpdateDetail = before;
            Notify($"The update failed: {ex.Message}", ToastKind.Error);
        }
    }
}
