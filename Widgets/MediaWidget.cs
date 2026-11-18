using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;
using Windows.Media.Control;

namespace QiDock.Widgets;

/// <summary>Now playing tile with previous / play-pause / next for the system media session.</summary>
public sealed partial class MediaWidget : DockWidget
{
    private const double ProgressWidth = 96;

    private readonly Border _tile;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly ContentControl _playPause;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Image _art;
    private readonly Border _artFrame;
    private readonly Border _progressFill;
    private readonly DispatcherTimer _progressTimer;
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _refreshVersion;
    private bool _started;
    private bool _live = true;
    private bool _playing;
    private string? _thumbnailKey;
    private string? _artSignature;   // which picture the cover shows, so an unchanged one is not redrawn
    private string _album = "";

    public MediaWidget()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Cover on the left, title / artist / progress in the middle, previous - play - next on the right.
        _art = new Image { Stretch = Stretch.UniformToFill };
        _artFrame = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(7), ClipToBounds = true, Background = Ui.Solid("#33FFFFFF"), VerticalAlignment = VerticalAlignment.Center };
        var artLayers = new Grid();
        artLayers.Children.Add(new ContentControl { Content = Ui.Icon("IconMusic", 15, Ui.WhiteSoft), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        artLayers.Children.Add(_art);
        _artFrame.Child = artLayers;
        _artFrame.SizeChanged += (_, e) => artLayers.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);

        _title = Ui.Text(size: 11.5, weight: FontWeights.SemiBold);
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _artist = Ui.Text(size: 10, brushKey: "TextSecondary");
        _artist.TextTrimming = TextTrimming.CharacterEllipsis;
        _artist.Margin = new Thickness(0, -1, 0, 3);
        var track = new Border { Width = ProgressWidth, Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
        track.SetResourceReference(Border.BackgroundProperty, "WidgetTrack");
        _progressFill = new Border { Width = 0, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
        _progressFill.SetResourceReference(Border.BackgroundProperty, "TextPrimary");
        track.Child = _progressFill;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 8, 0), Width = ProgressWidth + 14 };
        text.Children.Add(_title);
        text.Children.Add(_artist);
        text.Children.Add(track);

        _playPause = new ContentControl { Content = Ui.Glyph("GlyphPlay", 11), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _previous = Ui.IconButton(Ui.Glyph("GlyphPrevious", 10), async (_, _) => await Run(s => s.TrySkipPreviousAsync()), UiText.Get("MediaPrevious"), 24, 24);
        var playFace = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Child = _playPause };
        playFace.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        var play = Ui.IconButton(playFace, async (_, _) => await Run(s => s.TryTogglePlayPauseAsync()), UiText.Get("MediaPlayPause"), 30, 30);
        _next = Ui.IconButton(Ui.Glyph("GlyphNext", 10), async (_, _) => await Run(s => s.TrySkipNextAsync()), UiText.Get("MediaNext"), 24, 24);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(_previous);
        controls.Children.Add(play);
        controls.Children.Add(_next);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_artFrame);
        row.Children.Add(text);
        row.Children.Add(controls);

        _tile = new Border
        {
            Child = row,
            CornerRadius = (CornerRadius)Application.Current.FindResource("WidgetRadius"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _tile.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        _tile.SetResourceReference(Border.BorderBrushProperty, "WidgetTileStroke");
        _tile.SetResourceReference(FrameworkElement.HeightProperty, "WidgetTileHeight");

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) =>
        {
            UpdateProgress();
            if (CardOpen) ShowVolume();
        };
        WireCard();
    }

    public override string Id => WidgetIds.Media;
    public override FrameworkElement View => _tile;

    public override async void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.InvokeAsync(AttachCurrentSession);
            _manager.SessionsChanged += (_, _) => _dispatcher.InvokeAsync(AttachCurrentSession);
            AttachCurrentSession();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _manager = null;
            ShowIdle();
        }
    }

    public override void Stop()
    {
        _started = false;
        _popup?.Close();
        _progressTimer.Stop();
        Detach();
        _manager = null;
    }

    public override void SetLive(bool live)
    {
        _live = live;
        UpdateProgressTimer();
    }

    private void UpdateProgressTimer()
    {
        if (_started && (_live || CardOpen) && (_playing || CardOpen) && _tile.Visibility == Visibility.Visible)
        {
            UpdateProgress();
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
            UpdateProgress();
        }
    }

    /// <summary>The bar follows the session's timeline, moved on by the time since its last report while playing.</summary>
    private void UpdateProgress()
    {
        double fraction = 0;
        try
        {
            if (_session?.GetTimelineProperties() is { } timeline)
            {
                var length = (timeline.EndTime - timeline.StartTime).TotalSeconds;
                var position = timeline.Position.TotalSeconds;
                if (_playing) position += (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
                if (length > 1) fraction = Math.Clamp(position / length, 0, 1);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }
        _progressFill.Width = ProgressWidth * fraction;
        if (CardOpen) UpdateCardProgress();
    }

    private void AttachCurrentSession()
    {
        if (_manager is null) return;
        GlobalSystemMediaTransportControlsSession? session;
        try
        {
            session = _manager.GetCurrentSession() ?? _manager.GetSessions().FirstOrDefault();
        }
        catch (COMException)
        {
            session = null;
        }

        if (!ReferenceEquals(session, _session))
        {
            Detach();
            _session = session;
            if (session is not null)
            {
                session.MediaPropertiesChanged += Session_Changed;
                session.PlaybackInfoChanged += Session_Changed;
            }
        }
        _ = RefreshAsync();
    }

    private void Detach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= Session_Changed;
        _session.PlaybackInfoChanged -= Session_Changed;
        _session = null;
    }

    private void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _dispatcher.InvokeAsync(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        var session = _session;
        if (session is null)
        {
            ShowIdle();
            return;
        }

        try
        {
            var playback = session.GetPlaybackInfo();
            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion) return;

            var title = properties?.Title ?? "";
            var artist = properties?.Artist ?? "";
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
            {
                ShowIdle();
                return;
            }

            var text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} · {artist}";
            _title.Text = string.IsNullOrWhiteSpace(title) ? artist : title;
            _artist.Text = string.IsNullOrWhiteSpace(title) ? "" : artist;
            _tile.ToolTip = text;
            var playing = _playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _playPause.Content = Ui.Glyph(playing ? "GlyphPause" : "GlyphPlay", 11);
            _previous.IsEnabled = playback?.Controls?.IsPreviousEnabled != false;
            _next.IsEnabled = playback?.Controls?.IsNextEnabled != false;

            _album = properties?.AlbumTitle ?? "";
            // Browsers first send their own logo as the cover and the real one a moment later for the same
            // song, so the cover is read again on every change (and once more shortly after a new song).
            var key = $"{title}|{artist}|{properties?.AlbumTitle}";
            var newSong = key != _thumbnailKey;
            _thumbnailKey = key;
            await LoadArtworkAsync(properties?.Thumbnail, version, newSong);
            if (version != _refreshVersion) return;
            if (newSong) RefreshAgainSoon();
            SetVisible(true);
            UpdateProgressTimer();
            if (CardOpen) FillCard();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (version == _refreshVersion) ShowIdle();
        }
    }

    private void RefreshAgainSoon()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = RefreshAsync();
        };
        timer.Start();
    }

    private async Task LoadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference, int version, bool newSong)
    {
        if (reference is null)
        {
            // A player may leave the cover out for a moment (pausing); the last one stays for the same song.
            if (!newSong) return;
            _art.Source = null;
            _artSignature = null;
            TintCard(null);
            return;
        }

        try
        {
            using var winStream = await reference.OpenReadAsync();
            using var source = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await source.CopyToAsync(memory);
            memory.Position = 0;
            if (version != _refreshVersion) return;
            // The same picture again: nothing to redraw.
            var signature = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length)));
            if (signature == _artSignature && _art.Source is not null) return;
            _artSignature = signature;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 192; // sharp in the card's large cover too
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            _art.Source = image;
            TintCard(image);
        }
        catch (Exception ex) when (ex is COMException or IOException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            _art.Source = null;
            TintCard(null);
        }
    }

    /// <summary>
    /// Nothing playing: the tile stays (it used to vanish, and then it could be found neither in the dock nor in
    /// the add list) and says so; its play button sends the keyboard's play key, which resumes the last player.
    /// </summary>
    private void ShowIdle()
    {
        _playing = false;
        _thumbnailKey = null;
        _artSignature = null;
        _art.Source = null;
        _album = "";
        TintCard(null);
        _title.Text = UiText.Get("MediaIdleTitle");
        _artist.Text = UiText.Get("MediaIdle");
        _tile.ToolTip = UiText.Get("MediaIdleHint");
        _playPause.Content = Ui.Glyph("GlyphPlay", 11);
        _previous.IsEnabled = _next.IsEnabled = false;
        SetVisible(true);
        UpdateProgressTimer();
        if (CardOpen) FillCard();
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    private async Task Run(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        if (_session is null)
        {
            // The media play/pause key: Windows hands it to the player that last had sound (Spotify, the browser…).
            keybd_event(0xB3, 0, 0, 0);
            keybd_event(0xB3, 0, 2 /* key up */, 0);
            return;
        }
        try
        {
            await command(_session);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Media command failed: {0}", ex.Message);
        }
    }

    private void SetVisible(bool visible)
    {
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_tile.Visibility == target) return;
        _tile.Visibility = target;
        UpdateProgressTimer();
        RaiseLayoutChanged();
    }
}
