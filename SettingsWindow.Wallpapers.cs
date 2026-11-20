using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QiDock.Services;

namespace QiDock;

/// <summary>
/// Settings → Wallpaper: a gallery of pictures for the desktop. It is built the first time the tab is opened, so the
/// rest of the time it costs nothing; its small previews leave memory with the settings window.
/// </summary>
public partial class SettingsWindow
{
    private const int MarketsPerStep = 2;
    private bool _wallpapersStarted;
    private int _marketsShown;
    private readonly HashSet<string> _wallpapersSeen = new(StringComparer.Ordinal);
    private readonly List<(Border Tile, WallpaperChoice Choice)> _wallpaperTiles = [];
    private readonly CancellationTokenSource _wallpaperCancel = new();
    private readonly SemaphoreSlim _previewSlots = new(6);
    private bool _wallpaperBusy;

    private void StartWallpaperGallery()
    {
        if (_wallpapersStarted) return;
        _wallpapersStarted = true;
        Closed += (_, _) => _wallpaperCancel.Cancel();
        foreach (var choice in WallpaperGallery.Windows()) WindowsWallpapers.Children.Add(Tile(choice));
        _ = MoreDailyAsync();
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == SettingsTabs && SettingsTabs.SelectedItem == WallpaperTab) StartWallpaperGallery();
    }

    private async Task MoreDailyAsync()
    {
        MoreWallpapersButton.IsEnabled = false;
        if (_marketsShown == 0) WallpaperStatus.Text = UiText.Get("WallpaperLoading");
        try
        {
            var found = await WallpaperGallery.BingAsync(_marketsShown, MarketsPerStep, _wallpapersSeen, _wallpaperCancel.Token);
            _marketsShown += MarketsPerStep;
            foreach (var choice in found) DailyWallpapers.Children.Add(Tile(choice));
            if (WallpaperStatus.Text == UiText.Get("WallpaperLoading")) WallpaperStatus.Text = "";
            if (DailyWallpapers.Children.Count == 0) WallpaperStatus.Text = UiText.Get("WallpaperOffline");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        MoreWallpapersButton.IsEnabled = true;
        MoreWallpapersButton.Visibility = DailyWallpapers.Children.Count > 0 && _marketsShown < WallpaperGallery.MarketCount ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreWallpapers_Click(object sender, RoutedEventArgs e) => _ = MoreDailyAsync();

    private Border Tile(WallpaperChoice choice)
    {
        var tile = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(4),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = choice.Title,
            Child = new TextBlock
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Text = UiText.Get("WallpaperDownloading")
            }
        };
        tile.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        System.Windows.Automation.AutomationProperties.SetName(tile, choice.Title);
        tile.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) tile.Height = Math.Round(e.NewSize.Width * 9 / 16);
        };
        tile.MouseLeftButtonUp += (_, _) => _ = ChooseAsync(tile, choice);
        tile.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            _ = ChooseAsync(tile, choice);
        };
        _wallpaperTiles.Add((tile, choice));
        MarkCurrent(tile, choice, WallpaperGallery.Current());
        _ = PreviewAsync(tile, choice);
        return tile;
    }

    private async Task PreviewAsync(Border tile, WallpaperChoice choice)
    {
        try
        {
            await _previewSlots.WaitAsync(_wallpaperCancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (await WallpaperGallery.ThumbnailAsync(choice, _wallpaperCancel.Token) is { } picture)
                tile.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        }
        finally
        {
            _previewSlots.Release();
        }
    }

    private async Task ChooseAsync(Border tile, WallpaperChoice choice)
    {
        if (_wallpaperBusy) return;
        _wallpaperBusy = true;
        var label = (TextBlock)tile.Child;
        if (choice.Online)
        {
            label.Visibility = Visibility.Visible;
            tile.Opacity = 0.75;
        }
        WallpaperStatus.Text = choice.Online ? UiText.Get("WallpaperDownloading") : "";
        try
        {
            var file = await WallpaperGallery.ApplyAsync(choice, _wallpaperCancel.Token);
            WallpaperStatus.Text = UiText.Get(file is null ? "WallpaperFailed" : "WallpaperSet");
            if (file is not null)
                foreach (var (other, otherChoice) in _wallpaperTiles) MarkCurrent(other, otherChoice, file);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Wallpaper gallery: picture could not be downloaded ({ex.GetType().Name})");
            WallpaperStatus.Text = UiText.Get("WallpaperOffline");
        }
        finally
        {
            _wallpaperBusy = false;
            label.Visibility = Visibility.Collapsed;
            tile.Opacity = 1;
        }
    }

    private async void PickWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"{UiText.Get("WallpaperFiles")}|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != true) return;
        var file = await WallpaperGallery.ApplyAsync(new WallpaperChoice(dialog.FileName, Path.GetFileName(dialog.FileName), dialog.FileName, dialog.FileName, false));
        WallpaperStatus.Text = UiText.Get(file is null ? "WallpaperFailed" : "WallpaperSet");
        if (file is not null)
            foreach (var (tile, choice) in _wallpaperTiles) MarkCurrent(tile, choice, file);
    }

    /// <summary>A ring in the accent colour around the picture the desktop shows.</summary>
    private static void MarkCurrent(Border tile, WallpaperChoice choice, string? current)
    {
        var shown = current is not null && string.Equals(current, WallpaperGallery.FileOf(choice), StringComparison.OrdinalIgnoreCase);
        if (shown) tile.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        else tile.BorderBrush = Brushes.Transparent;
    }
}
