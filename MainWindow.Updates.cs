using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using QiDock.Services;

namespace QiDock;

/// <summary>"Güncellemeleri denetle" in the dock menu, and the quiet once-a-day check.</summary>
public partial class MainWindow
{
    private bool _updating;

    private void StartDailyUpdateCheck()
    {
        Updater.CleanUp();
        // Well after start-up, so it never competes with the dock appearing.
        var later = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(90) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            Updater.CheckDaily(release => Widgets.Ui.ShowHud("IconArrowDown", UiText.Format("UpdateAvailableHud", release.Version)));
        };
        later.Start();
    }

    private async void CheckForUpdates_Click()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            Widgets.Ui.ShowHud("IconRotate", UiText.Get("UpdateChecking"));
            Updater.Release? release;
            try
            {
                release = await Updater.CheckAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Widgets.Ui.ShowHud("IconWifiOff", UiText.Get("UpdateCheckFailed"));
                return;
            }
            if (release is null)
            {
                Widgets.Ui.ShowHud("IconCheck", UiText.Format("UpdateUpToDate", Updater.Current));
                return;
            }
            var answer = MessageBox.Show(
                UiText.Format("UpdatePrompt", release.Version, Updater.Current, Math.Max(1, release.Size / (1024 * 1024))),
                "QiDock",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;
            var progress = new Progress<int>(percent => Widgets.Ui.ShowHud("IconArrowDown", UiText.Format("UpdateDownloading", percent)));
            var error = await Updater.InstallAsync(release, progress);
            if (error is not null)
            {
                MessageBox.Show(error, "QiDock", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ((App)Application.Current).Restart();
        }
        finally
        {
            _updating = false;
        }
    }
}
