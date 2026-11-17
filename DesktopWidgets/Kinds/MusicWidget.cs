using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using static QiDock.DesktopWidgets.DeskUi;

namespace QiDock.DesktopWidgets.Kinds;

/// <summary>Now playing from any app with a media session (Spotify, browsers, Media Player).</summary>
internal sealed class MusicWidget : DeskWidget
{
    private Border? _root;
    private TextBlock? _title, _artist, _elapsed, _total;
    private Border? _progressFill;
    private Grid? _progress;
    private DeskButton? _play;
    private string? _shownKey;

    public override TimeSpan? Interval => TimeSpan.FromSeconds(1);

    public override FrameworkElement Build()
    {
        _shownKey = null;
        var padding = Desk.Padding(Size);
        _root = new Border { Margin = new Thickness(-padding.Left, -padding.Top, -padding.Right, -padding.Bottom), Padding = padding };
        Render();
        return _root;
    }

    protected override void OnAttach()
    {
        MediaHub.Instance.Subscribe(Render);
        Render();
    }
    protected override void OnDetach() => MediaHub.Instance.Unsubscribe(Render);

    private void Render()
    {
        if (_root is null) return;
        var hub = MediaHub.Instance;
        var key = hub.HasMedia ? $"{hub.Title}|{hub.Artist}|{hub.Art?.GetHashCode()}" : "";
        if (key != _shownKey)
        {
            _shownKey = key;
            _root.Background = hub.HasMedia ? Tint(hub.ArtColor) : null;
            _root.Child = hub.HasMedia ? Layout(hub) : Idle();
        }
        Tick();
    }

    private static Brush Tint(Color c)
    {
        static Color Scale(Color color, double f) => Color.FromRgb((byte)(color.R * f), (byte)(color.G * f), (byte)(color.B * f));
        var brush = new LinearGradientBrush(Scale(c, 0.55), Scale(c, 0.22), new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private FrameworkElement Idle()
    {
        _title = _artist = _elapsed = _total = null;
        _progress = null;
        _play = null;
        return Empty("IconMusic", Size is DeskSize.Mini or DeskSize.Strip ? Desk.L("Çalan bir şey yok", "Nothing playing") : Desk.L("Şu an bir şey çalmıyor.\nSpotify ya da tarayıcıda müzik açın.", "Nothing is playing.\nStart music in Spotify or a browser."));
    }

    private FrameworkElement Art(MediaHub hub, double size, double radius)
    {
        var border = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = Inset };
        if (hub.Art is { } art)
            border.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        else
            border.Child = DeskUi.Icon("IconMusic", size * 0.36, Secondary);
        border.Effect = size >= 100 ? new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.4, RenderingBias = RenderingBias.Performance } : null;
        return border;
    }

    private FrameworkElement Controls(double size)
    {
        _play = DeskButton.Round("GlyphPlay", () => MediaHub.Instance.Command(s => s.TryTogglePlayPauseAsync()), size, Brushes.White, Brushes.Black, size * 0.38);
        var previous = new DeskButton(Glyph("GlyphPrevious", size * 0.34), () => MediaHub.Instance.Command(s => s.TrySkipPreviousAsync()), size * 0.86);
        var next = new DeskButton(Glyph("GlyphNext", size * 0.34), () => MediaHub.Instance.Command(s => s.TrySkipNextAsync()), size * 0.86);
        return Row(previous, Margin(_play, 4, 0, 4), next);
    }

    private FrameworkElement ProgressBar(bool times)
    {
        _progressFill = new Border { CornerRadius = new CornerRadius(2), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        _progress = new Grid { Height = 4 };
        _progress.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Ui("#40FFFFFF") });
        _progress.Children.Add(_progressFill);
        if (!times)
        {
            _elapsed = _total = null;
            return _progress;
        }
        _elapsed = Digits("", 11, FontWeights.SemiBold, Ui("#B3FFFFFF"));
        _total = Digits("", 11, FontWeights.SemiBold, Ui("#B3FFFFFF"));
        var labels = Grid(columns: "*,Auto");
        labels.Children.Add(_elapsed);
        labels.Children.Add(At(_total, 0, 1));
        labels.Margin = new Thickness(0, 4, 0, 0);
        return Column(_progress, labels);
    }

    private static Brush Ui(string hex) => QiDock.Widgets.Ui.Solid(hex);

    private FrameworkElement Layout(MediaHub hub)
    {
        _title = T(hub.Title, 14, FontWeights.SemiBold, Brushes.White);
        _artist = T(hub.Artist, 12.5, FontWeights.Medium, Ui("#C8FFFFFF"));
        switch (Size)
        {
            case DeskSize.Mini:
            {
                _title.FontSize = 12.5;
                var grid = Grid(columns: "Auto,*,Auto");
                grid.Children.Add(Art(hub, 48, 10));
                grid.Children.Add(At(Margin(Column(_title, _artist), 10, 0, 6), 0, 1));
                _artist.FontSize = 11;
                _play = DeskButton.Round("GlyphPlay", () => MediaHub.Instance.Command(s => s.TryTogglePlayPauseAsync()), 32, Brushes.White, Brushes.Black, 12);
                grid.Children.Add(At(_play, 0, 2));
                _progress = null;
                return grid;
            }
            case DeskSize.Strip:
            {
                var grid = Grid(columns: "Auto,*,Auto");
                grid.Children.Add(Art(hub, 54, 11));
                var text = Column(_title, _artist, Margin(ProgressBar(false), 0, 7, 8, 0));
                grid.Children.Add(At(Margin(text, 12, 0, 8), 0, 1));
                grid.Children.Add(At(Controls(34), 0, 2));
                return grid;
            }
            case DeskSize.Small:
            {
                var grid = Grid("*,Auto,Auto,Auto");
                var art = Art(hub, 70, 12);
                art.HorizontalAlignment = HorizontalAlignment.Left;
                grid.Children.Add(art);
                grid.Children.Add(At(_title, 1));
                grid.Children.Add(At(_artist, 2));
                _play = DeskButton.Round("GlyphPlay", () => MediaHub.Instance.Command(s => s.TryTogglePlayPauseAsync()), 34, Brushes.White, Brushes.Black, 13);
                _play.HorizontalAlignment = HorizontalAlignment.Right;
                _play.VerticalAlignment = VerticalAlignment.Top;
                grid.Children.Add(_play);
                grid.Children.Add(At(Margin(ProgressBar(false), 0, 8), 3));
                return grid;
            }
            case DeskSize.Medium:
            {
                var grid = Grid(columns: "Auto,*");
                grid.Children.Add(Art(hub, 130, 14));
                var app = T(hub.App, 11.5, FontWeights.SemiBold, Ui("#A6FFFFFF"));
                _title.FontSize = 16;
                var controls = Controls(38);
                controls.HorizontalAlignment = HorizontalAlignment.Center;
                var right = Grid("Auto,Auto,Auto,*,Auto,Auto");
                right.Children.Add(app);
                right.Children.Add(At(_title, 1));
                right.Children.Add(At(_artist, 2));
                right.Children.Add(At(ProgressBar(true), 4));
                right.Children.Add(At(Margin(controls, 0, 4), 5));
                grid.Children.Add(At(Margin(right, 14), 0, 1));
                return grid;
            }
            default:
            {
                _title.FontSize = 18;
                _title.HorizontalAlignment = HorizontalAlignment.Center;
                _artist.HorizontalAlignment = HorizontalAlignment.Center;
                var art = Art(hub, 168, 18);
                art.HorizontalAlignment = HorizontalAlignment.Center;
                var controls = Controls(44);
                controls.HorizontalAlignment = HorizontalAlignment.Center;
                return Column(art, Margin(_title, 0, 14), _artist, Margin(ProgressBar(true), 0, 12), Margin(controls, 0, 6));
            }
        }
    }

    public override void Tick()
    {
        var hub = MediaHub.Instance;
        if (_play is not null) _play.Child = Glyph(hub.Playing ? "GlyphPause" : "GlyphPlay", _play.Width * 0.38, Brushes.Black);
        if (_progress is null || _progressFill is null) return;
        var duration = hub.Duration;
        var position = hub.LivePosition;
        var fraction = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        _progressFill.Width = (_progress.ActualWidth > 0 ? _progress.ActualWidth : 200) * fraction;
        if (_elapsed is not null) _elapsed.Text = Format(position);
        if (_total is not null) _total.Text = duration > TimeSpan.Zero ? "-" + Format(duration - position) : "";
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
