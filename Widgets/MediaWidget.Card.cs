using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using QiDock.Services;
using QiDock.Services.Widgets;
using Windows.Media.Control;

namespace QiDock.Widgets;

/// <summary>
/// The card that opens from the music tile: a large cover, the song, a bar to move through it, ten seconds back
/// and forward, previous / play / next and the player's volume. The card takes its colour from the cover.
/// </summary>
public sealed partial class MediaWidget
{
    private static readonly Color IdleTop = Color.FromRgb(0x3A, 0x3B, 0x46), IdleBottom = Color.FromRgb(0x1C, 0x1C, 0x22);

    private WidgetPopup? _popup;
    private Border? _card;
    private GradientStop? _topStop, _bottomStop;
    private Image? _cardArt;
    private TextBlock? _cardTitle, _cardArtist, _cardAlbum, _cardSource, _elapsed, _remaining, _volumeText, _volumeLabel;
    private Slider? _seek, _volume;
    private System.Windows.Shapes.Path? _cardPlayPause, _volumeIcon;
    private Button? _cardPrevious, _cardNext, _back10, _forward10;
    private bool _seeking, _settingVolume, _canSeek;
    private DockPanel? _times;
    private AudioSessions? _sessions;
    private AudioSessions.App? _playerAudio;
    private AudioEndpoint? _endpoint;
    private Color _coverColor = IdleTop;

#if DEBUG
    /// <summary>Debug snapshot run: opens the card without touching the player.</summary>
    internal void RehearseCard()
    {
        ToggleCard();
        OpenAudio();
        FillCard();
    }
#endif

    private bool CardOpen => _popup?.IsOpen == true;
    public override bool IsPopupOpen => CardOpen;
    public override void ClosePopup() => _popup?.Close();

    /// <summary>The tile's free space (not its buttons) opens the card.</summary>
    private void WireCard()
    {
        _tile.Cursor = Cursors.Hand;
        _tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleCard();
        };
    }

    private void ToggleCard()
    {
        if (_popup is null)
        {
            _popup = new WidgetPopup(_tile);
            _popup.SetContent(BuildCard(), 340);
            _popup.Opened += (_, _) =>
            {
                OpenAudio();
                FillCard();
                UpdateProgressTimer();
            };
            _popup.Closed += (_, _) =>
            {
                Flip(animate: false); // opens on the song again next time
                CloseAudio();
                UpdateProgressTimer();
                if (_idle && !_editing) SetVisible(false); // the music stopped while the card was open
            };
        }
        FillCard();
        _popup.Toggle();
    }

    private Border BuildCard()
    {
        // The card's own colours: white writing on the cover's colour, whatever the theme.
        _topStop = new GradientStop(_coverColor, 0);
        _bottomStop = new GradientStop(Darker(_coverColor, 0.45), 1);
        var fill = new LinearGradientBrush(new GradientStopCollection { _topStop, _bottomStop }, new Point(0, 0), new Point(0.35, 1));

        _cardArt = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(_cardArt, BitmapScalingMode.HighQuality);
        var artLayers = new Grid();
        artLayers.Children.Add(new ContentControl { Content = Ui.Icon("IconMusic", 30, Ui.WhiteSoft), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        artLayers.Children.Add(_cardArt);
        artLayers.Clip = new RectangleGeometry(new Rect(0, 0, 96, 96), 12, 12);
        var artFrame = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(12), Background = Ui.Solid("#26FFFFFF"), Child = artLayers };

        _cardTitle = Ui.Text(size: 15.5, weight: FontWeights.SemiBold);
        _cardTitle.TextWrapping = TextWrapping.Wrap;
        _cardTitle.MaxHeight = 42;
        _cardTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _cardArtist = Ui.Text(size: 12.5);
        _cardArtist.TextTrimming = TextTrimming.CharacterEllipsis;
        _cardArtist.Margin = new Thickness(0, 2, 0, 0);
        _cardAlbum = Ui.Text(size: 11, brushKey: "TextSecondary");
        _cardAlbum.TextTrimming = TextTrimming.CharacterEllipsis;
        _cardSource = Ui.Text(size: 10.5, brushKey: "TextSecondary");
        _cardSource.Margin = new Thickness(0, 6, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        words.Children.Add(_cardTitle);
        words.Children.Add(_cardArtist);
        words.Children.Add(_cardAlbum);
        words.Children.Add(_cardSource);
        var top = new DockPanel();
        DockPanel.SetDock(artFrame, Dock.Left);
        top.Children.Add(artFrame);
        top.Children.Add(words);

        // Where in the song: drag or click the bar to jump there.
        _seek = new Slider { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0) };
        _seek.SetResourceReference(FrameworkElement.StyleProperty, "WidgetSlider");
        _seek.ToolTip = UiText.Get("MediaSeek");
        _seek.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _seek.PreviewMouseLeftButtonUp += async (_, _) =>
        {
            await SeekTo(TimeSpan.FromSeconds(_seek.Value));
            _seeking = false;
        };
        _seek.LostMouseCapture += (_, _) => _seeking = false;
        _elapsed = Ui.Text("0:00", 10.5, brushKey: "TextSecondary");
        _remaining = Ui.Text("", 10.5, brushKey: "TextSecondary");
        var times = _times = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
        DockPanel.SetDock(_remaining, Dock.Right);
        times.Children.Add(_remaining);
        times.Children.Add(_elapsed);

        // Ten seconds back, previous, play / pause, next, ten seconds forward.
        _back10 = Ui.IconButton(Jump(forward: false), async (_, _) => await SeekBy(-10), UiText.Get("MediaBack10"), 40, 40);
        _cardPrevious = Ui.IconButton(Ui.Glyph("GlyphPrevious", 15), async (_, _) => await Run(s => s.TrySkipPreviousAsync()), UiText.Get("MediaPrevious"), 44, 40);
        _cardPlayPause = Ui.Glyph("GlyphPlay", 18, Ui.Solid("#FF141418"), brushKey: null);
        var playFace = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Background = Ui.Solid("#F2FFFFFF"), Child = _cardPlayPause };
        var play = Ui.IconButton(playFace, async (_, _) => await Run(s => s.TryTogglePlayPauseAsync()), UiText.Get("MediaPlayPause"), 58, 58);
        _cardNext = Ui.IconButton(Ui.Glyph("GlyphNext", 15), async (_, _) => await Run(s => s.TrySkipNextAsync()), UiText.Get("MediaNext"), 44, 40);
        _forward10 = Ui.IconButton(Jump(forward: true), async (_, _) => await SeekBy(10), UiText.Get("MediaForward10"), 40, 40);
        var controls = new UniformGrid { Columns = 5, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var button in new[] { _back10, _cardPrevious, play, _cardNext, _forward10 })
        {
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            controls.Children.Add(button);
        }

        // The computer's volume.
        _volumeIcon = Ui.Icon("IconVolume", 14);
        var mute = Ui.IconButton(_volumeIcon, (_, _) => ToggleMute(), UiText.Get("MediaMute"), 28, 26);
        DockPanel.SetDock(mute, Dock.Left);
        _volumeText = Ui.Text("", 11, FontWeights.SemiBold);
        _volumeText.Width = 34;
        _volumeText.TextAlignment = TextAlignment.Right;
        _volumeText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_volumeText, Dock.Right);
        _volume = new Slider { Minimum = 0, Maximum = 100, Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        _volume.SetResourceReference(FrameworkElement.StyleProperty, "WidgetSlider");
        _volume.ValueChanged += (_, e) =>
        {
            if (_settingVolume) return;
            SetVolume((float)(e.NewValue / 100));
        };
        var volumeRow = new DockPanel();
        volumeRow.Children.Add(mute);
        volumeRow.Children.Add(_volumeText);
        volumeRow.Children.Add(_volume);
        _volumeLabel = Ui.Text("", 10.5, brushKey: "TextSecondary");
        _volumeLabel.Margin = new Thickness(4, 6, 0, 2);

        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(_seek);
        body.Children.Add(times);
        body.Children.Add(controls);
        body.Children.Add(_volumeLabel);
        body.Children.Add(volumeRow);

        // The back of the card: the songs played lately, each with its small cover. A click on the cover or the
        // title turns the card over (and back).
        _front = body;
        _back = BuildBack();
        _back.Visibility = Visibility.Collapsed;
        foreach (var face in new FrameworkElement[] { artFrame, words })
        {
            face.Cursor = Cursors.Hand;
            face.ToolTip = UiText.Get("MediaShowRecent");
            face.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Flip();
            };
        }
        var faces = new Grid();
        faces.Children.Add(_front);
        faces.Children.Add(_back);
        _card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 16, 16, 12), Background = fill, Child = faces,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _turn
        };
        _card.Resources["TextPrimary"] = Ui.White;
        _card.Resources["TextSecondary"] = Ui.Solid("#B8FFFFFF");
        _card.Resources["WidgetTrack"] = Ui.Solid("#38FFFFFF");
        _card.Resources["WidgetAccent"] = Ui.Solid("#F2FFFFFF");
        _card.Resources["SurfaceHover"] = Ui.Solid("#24FFFFFF");
        TextElement.SetForeground(_card, Ui.White);
        return _card;
    }

    // ---- The back of the card: the browser's playlist, or the songs played lately ----

    private sealed record Played(string Key, string Title, string Artist, ImageSource? Cover);

    private readonly List<Played> _played = [];
    private readonly ScaleTransform _turn = new(1, 1);
    private FrameworkElement? _front;
    private DockPanel? _back;
    private StackPanel? _playedList;
    private ScrollViewer? _backScroll;
    private TextBlock? _backHeading;
    private bool _flipped;
    private object? _rememberedArt;
    private IReadOnlyList<PlaylistSong> _playlist = [];
    private System.Windows.Automation.AutomationElement? _hiddenTab;
    private int _playlistRead;
    // YouTube's small pictures, kept while QiDock runs (a few kilobytes each) so the list does not load them again.
    private static readonly Dictionary<string, ImageSource> Thumbnails = [];

    /// <summary>
    /// Keeps the songs played lately (newest first, at most twenty) with a small copy of each cover. Windows does
    /// not tell other apps a player's queue, so the back of the card shows what was played, not what comes next.
    /// </summary>
    private void Remember(string title, string artist, ImageSource? art)
    {
        var key = $"{title}|{artist}";
        var index = _played.FindIndex(p => p.Key == key);
        var cover = index >= 0 ? _played[index].Cover : null;
        if (art is not null && !ReferenceEquals(art, _rememberedArt))
        {
            _rememberedArt = art;
            cover = SmallCover(art) ?? cover;
        }
        if (index == 0 && ReferenceEquals(_played[0].Cover, cover)) return;
        if (index >= 0) _played.RemoveAt(index);
        _played.Insert(0, new Played(key, title, artist, cover));
        if (_played.Count > 20) _played.RemoveAt(_played.Count - 1);
        if (!_flipped) return;
        FillBack();
        if (index != 0) ReadPlaylist(); // another song: the list moved on
    }

    /// <summary>
    /// Reads the list beside the video in the browser (only while the back is open; reading it makes the browser
    /// keep its screen-reader view of the page). <paramref name="tries"/>: a tab just brought forward needs a moment.
    /// </summary>
    private async void ReadPlaylist(int tries = 1)
    {
        if (!BrowserPlaylist.IsBrowser(_session?.SourceAppUserModelId))
        {
            _playlist = [];
            _hiddenTab = null;
            return;
        }
        var read = ++_playlistRead;
        var view = await BrowserPlaylist.ReadAsync(_title.Text);
        if (read != _playlistRead || !_flipped) return;
        if (view.Songs.Count == 0 && tries > 1)
        {
            await Task.Delay(900);
            if (read == _playlistRead && _flipped) ReadPlaylist(tries - 1);
            return;
        }
        var first = _playlist.Count == 0 && view.Songs.Count > 0;
        _playlist = view.Songs;
        _hiddenTab = view.HiddenTab;
        FillBack(scrollToCurrent: first);
    }

    private async void PlaySong(PlaylistSong song)
    {
        if (!await BrowserPlaylist.PlayAsync(song)) return;
        _playlist = _playlist.Select(s => s with { Current = s.Index == song.Index }).ToList();
        FillBack();
    }

    private async void ShowHiddenTab()
    {
        if (_hiddenTab is not { } tab || !await BrowserPlaylist.ShowTabAsync(tab)) return;
        _hiddenTab = null;
        FillBack();
        await Task.Delay(700);
        ReadPlaylist(tries: 4);
    }

    private static ImageSource Thumbnail(PlaylistSong song)
    {
        if (Thumbnails.TryGetValue(song.VideoId, out var known)) return known;
        if (Thumbnails.Count > 120) Thumbnails.Clear();
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = song.Thumbnail;
        image.DecodePixelHeight = 84; // the cover is 42 points; enough for a 200% screen
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        Thumbnails[song.VideoId] = image;
        return image;
    }

    private static ImageSource? SmallCover(ImageSource art)
    {
        if (art is not BitmapSource bitmap || bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1) return null;
        try
        {
            var scale = Math.Min(1, 96.0 / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
            var small = new CachedBitmap(new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            small.Freeze();
            return small;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or COMException)
        {
            return null;
        }
    }

    private DockPanel BuildBack()
    {
        var back = Ui.Icon("IconChevronLeft", 13, Ui.White, null, 2.2);
        var backText = Ui.Text(UiText.Get("MediaShowPlaying"), 12, FontWeights.SemiBold);
        backText.Margin = new Thickness(5, 0, 0, 0);
        var backRow = new StackPanel { Orientation = Orientation.Horizontal };
        backRow.Children.Add(back);
        backRow.Children.Add(backText);
        var backButton = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(2, 4, 8, 4), Child = backRow, HorizontalAlignment = HorizontalAlignment.Left };
        backButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Flip();
        };
        var heading = _backHeading = Ui.Text(UiText.Get("MediaRecent"), 15.5, FontWeights.SemiBold);
        heading.Margin = new Thickness(2, 6, 0, 10);
        var top = new StackPanel();
        top.Children.Add(backButton);
        top.Children.Add(heading);
        DockPanel.SetDock(top, Dock.Top);
        _playedList = new StackPanel();
        // A slim bar shows there is more below; the wheel moves the list (and never anything behind the card).
        var scroll = _backScroll = new ScrollViewer { Content = _playedList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset - e.Delta * 0.6, 0, scroll.ScrollableHeight));
        };
        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(top);
        panel.Children.Add(scroll);
        return panel;
    }

    /// <summary>The back: the browser's list when it could be read, otherwise the songs played lately.</summary>
    private void FillBack(bool scrollToCurrent = false)
    {
        if (_playedList is null) return;
        _playedList.Children.Clear();
        if (_playlist.Count > 0)
        {
            _backHeading!.Text = UiText.Get("MediaPlaylist");
            Border? now = null;
            foreach (var song in _playlist)
            {
                var row = Row(Thumbnail(song), song.Title, song.Channel, song.Current, song.Current ? () => Flip() : () => PlaySong(song));
                if (song.Current) now = row;
                _playedList.Children.Add(row);
            }
            if (scrollToCurrent && now is not null)
            {
                // The playing song near the top, the ones coming after it below.
                _playedList.Dispatcher.BeginInvoke(() => _backScroll?.ScrollToVerticalOffset(Math.Max(0, now.TranslatePoint(new Point(), _playedList).Y - 50)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            return;
        }
        _backHeading!.Text = UiText.Get("MediaRecent");
        if (_hiddenTab is not null)
        {
            var hint = Ui.Text(UiText.Get("MediaShowTab"), 12, FontWeights.SemiBold);
            hint.TextWrapping = TextWrapping.Wrap;
            var button = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8),
                Background = Ui.Solid("#2EFFFFFF"), Cursor = Cursors.Hand, Child = hint
            };
            button.MouseEnter += (_, _) => button.Background = Ui.Solid("#40FFFFFF");
            button.MouseLeave += (_, _) => button.Background = Ui.Solid("#2EFFFFFF");
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowHiddenTab();
            };
            _playedList.Children.Add(button);
        }
        if (_played.Count == 0)
        {
            var empty = Ui.Text(UiText.Get("MediaRecentEmpty"), 12, brushKey: "TextSecondary");
            empty.Margin = new Thickness(2, 8, 0, 0);
            _playedList.Children.Add(empty);
            return;
        }
        var nowKey = $"{_title.Text}|{_artist.Text}";
        foreach (var song in _played)
        {
            var playing = song.Key == nowKey || _played.IndexOf(song) == 0 && !_idle;
            _playedList.Children.Add(Row(song.Cover, song.Title, song.Artist, playing, playing ? () => Flip() : null));
        }
    }

    /// <summary>One song: its small cover, the title and the artist; the playing one is lit and marked.</summary>
    private static Border Row(ImageSource? coverImage, string title, string artist, bool playing, Action? click)
    {
        var cover = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(8), Background = Ui.Solid("#26FFFFFF") };
        if (coverImage is not null) cover.Background = new ImageBrush(coverImage) { Stretch = Stretch.UniformToFill };
        else cover.Child = new ContentControl { Content = Ui.Icon("IconMusic", 16, Ui.WhiteSoft), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(title, 12.5, FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var by = Ui.Text(artist, 11, brushKey: "TextSecondary");
        by.TextTrimming = TextTrimming.CharacterEllipsis;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        words.Children.Add(name);
        if (artist.Length > 0) words.Children.Add(by);
        var line = new DockPanel();
        DockPanel.SetDock(cover, Dock.Left);
        line.Children.Add(cover);
        if (playing)
        {
            var mark = Ui.Icon("IconVolume", 13, Ui.White, null);
            mark.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(mark, Dock.Right);
            line.Children.Add(mark);
        }
        line.Children.Add(words);
        var idle = playing ? Ui.Solid("#24FFFFFF") : Brushes.Transparent;
        var row = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 5, 8, 5), Margin = new Thickness(0, 0, 0, 3), Child = line,
            Background = idle
        };
        if (click is null) return row;
        row.Cursor = Cursors.Hand;
        if (!playing)
        {
            row.MouseEnter += (_, _) => row.Background = Ui.Solid("#14FFFFFF");
            row.MouseLeave += (_, _) => row.Background = idle;
        }
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return row;
    }

    /// <summary>Turns the card over: it narrows to an edge, the other face comes up, it widens again.</summary>
    private void Flip(bool animate = true)
    {
        if (_front is null || _back is null) return;
        void Swap()
        {
            _flipped = !_flipped;
            if (_flipped)
            {
                FillBack(scrollToCurrent: true);
                ReadPlaylist();
                _back.Height = _front.ActualHeight; // the card keeps its size
                _front.Visibility = Visibility.Hidden;
                _back.Visibility = Visibility.Visible;
            }
            else
            {
                _front.Visibility = Visibility.Visible;
                _back.Visibility = Visibility.Collapsed;
            }
        }
        if (!animate)
        {
            _turn.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _turn.ScaleX = 1;
            if (_flipped) Swap();
            return;
        }
        var fold = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fold.Completed += (_, _) =>
        {
            Swap();
            _turn.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        _turn.BeginAnimation(ScaleTransform.ScaleXProperty, fold);
    }

    /// <summary>The round arrow of a ten-second jump, with "10" in its middle.</summary>
    private static Grid Jump(bool forward)
    {
        var arrow = Ui.Icon("IconRotate", 22);
        if (forward)
        {
            arrow.RenderTransformOrigin = new Point(0.5, 0.5);
            arrow.RenderTransform = new ScaleTransform(-1, 1);
        }
        var ten = Ui.Text("10", 7.5, FontWeights.Bold);
        ten.HorizontalAlignment = HorizontalAlignment.Center;
        ten.VerticalAlignment = VerticalAlignment.Center;
        ten.Margin = new Thickness(0, 1, 0, 0);
        var face = new Grid();
        face.Children.Add(arrow);
        face.Children.Add(ten);
        return face;
    }

    /// <summary>Puts the current song in the card (title, cover, buttons); the bar follows on the timer.</summary>
    private void FillCard()
    {
        if (_card is null) return;
        var idle = _session is null || _art.Source is null && _title.Text == UiText.Get("MediaIdleTitle");
        _cardTitle!.Text = _title.Text;
        _cardArtist!.Text = _artist.Text;
        _cardAlbum!.Text = _album;
        _cardAlbum.Visibility = string.IsNullOrWhiteSpace(_album) ? Visibility.Collapsed : Visibility.Visible;
        _cardSource!.Text = SourceName();
        _cardSource.Visibility = _cardSource.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _cardArt!.Source = _art.Source;
        _cardPlayPause!.Data = (Geometry)Application.Current.FindResource(_playing ? "GlyphPause" : "GlyphPlay");
        _cardPrevious!.IsEnabled = _previous.IsEnabled;
        _cardNext!.IsEnabled = _next.IsEnabled;
        _canSeek = !idle && CanSeek();
        UpdateCardProgress();
        ShowVolume();
    }

    private string SourceName()
    {
        var id = _session?.SourceAppUserModelId;
        if (string.IsNullOrWhiteSpace(id)) return "";
        var name = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : id;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Equals("msedge", StringComparison.OrdinalIgnoreCase)) name = "Edge";
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : "";
    }

    private bool CanSeek()
    {
        try { return _session?.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled == true; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { return false; }
    }

    /// <summary>Moves the bar and the two times; left alone while the bar is being dragged.</summary>
    private void UpdateCardProgress()
    {
        if (_seek is null || !CardOpen && _card?.IsLoaded != true) return;
        double position = 0, length = 0;
        try
        {
            if (_session?.GetTimelineProperties() is { } timeline)
            {
                length = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
                position = (timeline.Position - timeline.StartTime).TotalSeconds;
                if (_playing) position += (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
                position = Math.Clamp(position, 0, Math.Max(length, 0));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        // Players that do not tell the song's length (some sites in the browser) get no bar to drag.
        var known = length > 1;
        _seek.Visibility = _times!.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        _seek.IsEnabled = _back10!.IsEnabled = _forward10!.IsEnabled = known && _canSeek;
        if (_seeking) return;
        _seek.Maximum = Math.Max(1, length);
        _seek.Value = position;
        _elapsed!.Text = Clock(position);
        _remaining!.Text = length > 1 ? "-" + Clock(length - position) : "";
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private async Task SeekBy(double seconds)
    {
        if (_seek is null) return;
        await SeekTo(TimeSpan.FromSeconds(Math.Clamp(_seek.Value + seconds, 0, _seek.Maximum)));
    }

    private async Task SeekTo(TimeSpan position)
    {
        if (_session is null) return;
        try
        {
            var start = _session.GetTimelineProperties()?.StartTime ?? TimeSpan.Zero;
            await _session.TryChangePlaybackPositionAsync((start + position).Ticks);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        if (_seek is not null && !_seeking) _seek.Value = position.TotalSeconds;
        _elapsed!.Text = Clock(position.TotalSeconds);
    }

    // ---- Volume ----

    private void OpenAudio()
    {
        // The computer's own volume (what the keyboard's volume keys change), not the player's.
        CloseAudio();
        _endpoint = new AudioEndpoint();
    }

    private void CloseAudio()
    {
        _playerAudio = null;
        _sessions?.Dispose();
        _sessions = null;
        _endpoint?.Dispose();
        _endpoint = null;
    }

    private void ShowVolume()
    {
        if (_volume is null) return;
        float level;
        bool muted;
        if (_playerAudio is { } app)
        {
            level = app.Volume;
            muted = app.Muted;
            _volumeLabel!.Text = UiText.Format("MediaAppVolume", SourceName() is { Length: > 0 } name ? name : app.Name);
        }
        else if (_endpoint?.Read() is { } state)
        {
            level = state.Volume;
            muted = state.Muted;
            _volumeLabel!.Text = UiText.Get("MediaSystemVolume");
        }
        else return;
        _settingVolume = true;
        _volume.Value = Math.Round(level * 100);
        _settingVolume = false;
        _volumeText!.Text = UiText.Percent((int)_volume.Value);
        _volumeIcon!.Data = (Geometry)Application.Current.FindResource(muted || level <= 0 ? "IconVolumeMute" : "IconVolume");
    }

    private void SetVolume(float level)
    {
        try
        {
            if (_playerAudio is { } app)
            {
                app.Volume = level;
                if (level > 0 && app.Muted) app.Muted = false;
            }
            else _endpoint?.SetVolume(level);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
        ShowVolume();
    }

    private void ToggleMute()
    {
        try
        {
            if (_playerAudio is { } app) app.Muted = !app.Muted;
            else if (_endpoint?.Read() is { } state) _endpoint.SetMute(!state.Muted);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
        ShowVolume();
    }

    // ---- The cover's colour ----

    /// <summary>The card slides to the cover's colour (a soft grey without a cover).</summary>
    private void TintCard(BitmapSource? cover)
    {
        var color = cover is null ? IdleTop : CoverColor(cover);
        if (color == _coverColor) return;
        _coverColor = color;
        if (_topStop is null || _bottomStop is null) return;
        var bottom = cover is null ? IdleBottom : Darker(color, 0.45);
        var duration = TimeSpan.FromMilliseconds(CardOpen ? 450 : 0);
        _topStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(color, duration));
        _bottomStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(bottom, duration));
    }

    /// <summary>
    /// The cover's main colour: colourful, mid-bright pixels count most (not the black or white background), then
    /// the colour is brought to a depth where white writing reads well on it.
    /// </summary>
    private static Color CoverColor(BitmapSource cover)
    {
        try
        {
            var small = new TransformedBitmap(cover, new ScaleTransform(24.0 / cover.PixelWidth, 24.0 / cover.PixelHeight));
            var pixels = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            var width = pixels.PixelWidth;
            var height = pixels.PixelHeight;
            var data = new byte[width * height * 4];
            pixels.CopyPixels(data, width * 4, 0);
            double r = 0, g = 0, b = 0, weight = 0, ar = 0, ag = 0, ab = 0;
            for (var i = 0; i < data.Length; i += 4)
            {
                double pb = data[i] / 255.0, pg = data[i + 1] / 255.0, pr = data[i + 2] / 255.0;
                var max = Math.Max(pr, Math.Max(pg, pb));
                var min = Math.Min(pr, Math.Min(pg, pb));
                var saturation = max <= 0 ? 0 : (max - min) / max;
                var w = saturation * saturation * (max < 0.12 ? 0.1 : 1) + 0.002;
                r += pr * w; g += pg * w; b += pb * w; weight += w;
                ar += pr; ag += pg; ab += pb;
            }
            var count = data.Length / 4.0;
            (r, g, b) = weight > 0.02 * count ? (r / weight, g / weight, b / weight) : (ar / count, ag / count, ab / count);
            return Settle(r, g, b);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return IdleTop;
        }
    }

    /// <summary>Keeps the hue, holds the lightness near 32% and the saturation at most 70%.</summary>
    private static Color Settle(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var light = (max + min) / 2;
        double hue = 0, saturation = 0;
        if (max - min > 1e-6)
        {
            var d = max - min;
            saturation = light > 0.5 ? d / (2 - max - min) : d / (max + min);
            hue = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            hue /= 6;
        }
        saturation = Math.Min(saturation, 0.7);
        light = Math.Clamp(light, 0.26, 0.36);
        double Channel(double t)
        {
            var q = light < 0.5 ? light * (1 + saturation) : light + saturation - light * saturation;
            var p = 2 * light - q;
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        return Color.FromRgb((byte)(Channel(hue + 1 / 3.0) * 255), (byte)(Channel(hue) * 255), (byte)(Channel(hue - 1 / 3.0) * 255));
    }

    private static Color Darker(Color color, double amount) =>
        Color.FromRgb((byte)(color.R * (1 - amount)), (byte)(color.G * (1 - amount)), (byte)(color.B * (1 - amount)));
}
