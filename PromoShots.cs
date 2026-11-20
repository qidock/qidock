#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using QiDock.DesktopWidgets;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Notifications;
using QiDock.Services.Widgets;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// Debug builds only (<c>--promo-shots &lt;folder&gt; &lt;tr|en&gt; [steps]</c>, with QIDOCK_PROMO=1): the pictures the
/// promotional videos are made of, drawn by QiDock's own code with made-up data (see <see cref="Promo"/>): the dock in
/// every look, the dock's messages, the music card growing out of the dock, icons, desktop scenes, the settings pages, the Files
/// window and the app screen. Twice the screen's size, transparent around floating parts.
/// </summary>
internal static class PromoShots
{
    private const double Scale = 2;
    private static string _folder = "";
    private static readonly Dictionary<string, object> Layout = [];

    /// <summary>Apps everyone knows (and Windows' own): nothing personal or little known appears in the videos.</summary>
    private static readonly string[] DockApps =
    [
        "Microsoft.Windows.Explorer", "Chrome", "MSEdge", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "Telegram.TelegramDesktop",
        "Microsoft.Office.WINWORD.EXE.15", "Microsoft.Office.EXCEL.EXE.15", "Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows",
        "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsStore_8wekyb3d8bbwe!App",
        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"
    ];

    public static async void Render(string folder, string? only, Action done)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        var steps = new (string Name, Func<Task> Run)[]
        {
            ("dock", Docks), ("hud", Huds), ("music", MusicDesk), ("icons", Icons), ("desk", Desks),
            ("settings", Settings), ("files", Files), ("apps", Apps)
        };
        var wanted = only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (name, run) in steps)
        {
            if (wanted is { Length: > 0 } && !wanted.Contains(name)) continue;
            try
            {
                await run();
            }
            catch (Exception ex)
            {
                File.WriteAllText(System.IO.Path.Combine(folder, $"{name}.error.txt"), ex.ToString());
            }
        }
        var layoutFile = System.IO.Path.Combine(folder, "layout.json");
        var merged = new Dictionary<string, JsonElement>();
        if (File.Exists(layoutFile))
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(layoutFile)) ?? []) merged[pair.Key] = pair.Value;
        foreach (var pair in Layout) merged[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
        File.WriteAllText(layoutFile, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
        done();
    }

    /// <summary>The widgets' settings with a well-known city for the weather (not this computer owner's).</summary>
    private static WidgetSettings Options => ((App)Application.Current).Preferences.Current.WidgetOptions with
    {
        WeatherCity = UiText.Current.Language == AppLanguage.English ? "Paris" : "İstanbul"
    };
    private static double Opacity => ((App)Application.Current).Preferences.Current.SurfaceOpacity;

    // ---- Dock -------------------------------------------------------------------------------------------------

    private static async Task Docks()
    {
        var widgets = LiveWidgets();
        await Task.Delay(7000);
        var (dock, ring) = BuildDock(widgets);
        var host = Pad(dock, 36);
        // In a window (out of sight), so a new theme reaches every part of the dock as it does on the screen.
        var stage = Stage(host);
        foreach (var look in ThemeStyles.All)
        {
            ThemeManager.Apply(AppTheme.Dark, Opacity, look.Id);
            await Task.Delay(250);
            Save(host, $"dock-{look.Id}.png");
        }
        ThemeManager.Apply(AppTheme.Dark, Opacity, ThemeStyles.Glass);
        ring.Visibility = Visibility.Visible;
        await Task.Delay(250);
        Save(host, "dock-keyboard.png");
        ring.Visibility = Visibility.Collapsed;
        stage.Close();
        foreach (var widget in widgets) widget.Dispose();
    }

    private static DockWidget[] LiveWidgets()
    {
        DockWidget[] widgets = [new ClockWidget(), new WeatherWidget(), new ControlCenterWidget()];
        foreach (var widget in widgets)
        {
            widget.Current = Options;
            widget.ApplySettings(Options);
            widget.Start();
            widget.SetLive(true);
        }
        return widgets;
    }

    /// <summary>The dock as it stands on the screen: its material, the theme's pattern and band, app icons with running dots, and widget tiles.</summary>
    private static (Border Dock, Border Ring) BuildDock(IEnumerable<DockWidget> widgets, int apps = 12)
    {
        const double tile = 50, icon = 44;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Border();
        var index = 0;
        foreach (var id in DockApps.Take(apps))
        {
            if (NotificationApps.LoadIcon(id, 128, exact: true) is not { } image) continue;
            var cell = new Grid { Width = tile, Height = tile + 6, Margin = new Thickness(1.5, 0, 1.5, 0) };
            var picture = new Image { Source = image, Width = icon, Height = icon, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            cell.Children.Add(picture);
            if (index is 0 or 1 or 3 or 6)
            {
                var dot = new Ellipse { Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };
                dot.SetResourceReference(Shape.FillProperty, "IndicatorBrush");
                cell.Children.Add(dot);
            }
            if (index == 2)
            {
                ring = new Border
                {
                    Width = icon + 10, Height = icon + 10, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2.5),
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, 0, 0), Visibility = Visibility.Collapsed
                };
                ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                cell.Children.Add(ring);
            }
            row.Children.Add(cell);
            index++;
        }
        var list = widgets.ToList();
        if (list.Count > 0)
        {
            var line = new Border { Width = 1, Height = 34, Margin = new Thickness(9, 0, 9, 0), Opacity = 0.35 };
            line.SetResourceReference(Border.BackgroundProperty, "TextSecondary");
            row.Children.Add(line);
            foreach (var widget in list) row.Children.Add(new Border { Height = 44, Margin = new Thickness(2, 0, 2, 0), Child = widget.View });
        }
        var layers = new Grid();
        var pattern = new Border { Margin = new Thickness(-10, -6, -10, -6), CornerRadius = new CornerRadius(17), IsHitTestVisible = false };
        pattern.SetResourceReference(Border.BackgroundProperty, "DockPattern");
        var band = new Border { Margin = new Thickness(-10, 0, -10, -6), CornerRadius = new CornerRadius(0, 0, 17, 17), VerticalAlignment = VerticalAlignment.Bottom };
        band.SetResourceReference(Border.BackgroundProperty, "DockBand");
        band.SetResourceReference(FrameworkElement.HeightProperty, "DockBandHeight");
        layers.Children.Add(pattern);
        layers.Children.Add(band);
        layers.Children.Add(row);
        var dock = new Border
        {
            CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6), Child = layers,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 3, Direction = 270, Opacity = 0.38 }
        };
        dock.SetResourceReference(Border.BackgroundProperty, "DockMaterial");
        dock.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        dock.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "InterfaceFont");
        return (dock, ring);
    }

    // ---- The dock's messages (updates, keyboard) ------------------------------------------------------------

    private static Task Huds()
    {
        var hud = DockHud.Shared;
        (string Name, string Icon, string Text)[] messages =
        [
            ("hud-update-check", "IconRotate", UiText.Get("UpdateChecking")),
            ("hud-update-ready", "IconArrowDown", UiText.Format("UpdateAvailableHud", "0.4.0")),
            ("hud-update-download", "IconArrowDown", UiText.Format("UpdateDownloading", 64)),
            ("hud-update-done", "IconCheck", UiText.Format("UpdateUpToDate", "0.4.0")),
            ("hud-keyboard", "IconKeyboard", UiText.Get("DockKeysHint"))
        ];
        foreach (var (name, icon, text) in messages)
        {
            hud.RehearseMessage(icon, text);
            var panel = hud.RehearseDock();
            hud.RehearseMorph(1.0);
            Save(Pad(panel, 24), name + ".png");
            if (panel.Parent is Border border) border.Child = null;
        }
        return Task.CompletedTask;
    }

    // ---- Music card ---------------------------------------------------------------------------------------

    /// <summary>Made-up songs for the music card (no real artists).</summary>
    private static (string Title, string Channel, Color From, Color To)[] PromoSongs() =>
    [
        ("Midnight Drive", "Neon Harbor", Rgb(0x5B, 0x3C, 0xC4), Rgb(0xF2, 0x5C, 0x9A)),
        ("Golden Hour", "Lumen Trio", Rgb(0xFF, 0xB3, 0x47), Rgb(0xE8, 0x4A, 0x5F)),
        ("Ocean Lights", "Blue Atlas", Rgb(0x1F, 0x8E, 0xD6), Rgb(0x3E, 0xE0, 0xC8)),
        ("Paper Planes", "Kite & Cloud", Rgb(0x8E, 0xC5, 0x5A), Rgb(0x2E, 0x8B, 0x6E)),
        ("Velvet Rain", "Mira Sol", Rgb(0x7A, 0x4E, 0xD8), Rgb(0x34, 0x2A, 0x7A)),
        ("Slow Motion", "The Paper Moons", Rgb(0xF5, 0x7C, 0x51), Rgb(0x8A, 0x2B, 0x52)),
        ("Northern Sky", "Aurora Lane", Rgb(0x34, 0xD3, 0xA0), Rgb(0x2B, 0x5B, 0xD9)),
        ("City of Glass", "Echo Park", Rgb(0x9A, 0xA8, 0xBF), Rgb(0x3B, 0x47, 0x60))
    ];

    /// <summary>A made-up album cover: a soft two-colour blend with a ring and a wave.</summary>
    private static BitmapSource Cover(Color from, Color to, int seed)
    {
        const int size = 168;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1)), null, new Rect(0, 0, size, size));
            var light = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            switch (seed % 4)
            {
                case 0:
                    dc.DrawEllipse(null, new Pen(light, 10), new Point(size * 0.5, size * 0.5), size * 0.28, size * 0.28);
                    break;
                case 1:
                    dc.DrawEllipse(light, null, new Point(size * 0.68, size * 0.36), size * 0.2, size * 0.2);
                    break;
                case 2:
                    for (var i = 0; i < 4; i++) dc.DrawRectangle(light, null, new Rect(0, size * (0.52 + i * 0.1), size, size * 0.035));
                    break;
                default:
                    var wave = new StreamGeometry();
                    using (var g = wave.Open())
                    {
                        g.BeginFigure(new Point(0, size * 0.7), true, true);
                        for (var x = 0; x <= size; x += 4) g.LineTo(new Point(x, size * 0.62 + Math.Sin(x / 22.0) * 10), true, false);
                        g.LineTo(new Point(size, size), true, false);
                        g.LineTo(new Point(0, size), true, false);
                    }
                    dc.DrawGeometry(light, null, wave);
                    break;
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ---- Icons: a small icon blown up next to the full-resolution one -------------------------------------

    /// <summary>Well-known apps for the icon comparison.</summary>
    private static readonly string[] IconApps =
    [
        "Chrome", "MSEdge", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "Telegram.TelegramDesktop",
        "Microsoft.Office.WINWORD.EXE.15", "Microsoft.Office.EXCEL.EXE.15"
    ];

    private static Task Icons()
    {
        const double size = 96;
        foreach (var sharp in new[] { false, true })
        {
            var pictures = new List<FrameworkElement>();
            foreach (var id in IconApps)
            {
                var image = sharp ? NotificationApps.LoadIcon(id, (int)(size * Scale), exact: true) : Small(NotificationApps.LoadIcon(id, 32), 32);
                if (image is null) continue;
                var picture = new Image { Source = image, Width = size, Height = size, Margin = new Thickness(14) };
                RenderOptions.SetBitmapScalingMode(picture, sharp ? BitmapScalingMode.HighQuality : BitmapScalingMode.Linear);
                pictures.Add(picture);
            }
            var tag = sharp ? "after" : "before";
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var picture in pictures) row.Children.Add(Copy(picture));
            Save(row, $"icons-{tag}.png");
            var grid = new UniformGrid { Columns = 3 };
            foreach (var picture in pictures) grid.Children.Add(Copy(picture));
            Save(grid, $"icons-grid-{tag}.png");
        }
        return Task.CompletedTask;

        static Image Copy(FrameworkElement from)
        {
            var source = (Image)from;
            var image = new Image { Source = source.Source, Width = source.Width, Height = source.Height, Margin = source.Margin };
            RenderOptions.SetBitmapScalingMode(image, RenderOptions.GetBitmapScalingMode(source));
            return image;
        }
    }

    /// <summary>The icon at <paramref name="pixels"/> pixels at most (Windows may hand over a bigger one).</summary>
    private static BitmapSource? Small(BitmapSource? image, int pixels)
    {
        if (image is null || image.PixelWidth <= pixels) return image;
        var scale = (double)pixels / image.PixelWidth;
        var small = new CachedBitmap(new TransformedBitmap(image, new ScaleTransform(scale, scale)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        small.Freeze();
        return small;
    }

    // ---- The music card growing out of its tile in the dock, over the wallpaper -----------------------------

    /// <summary>
    /// The dock with the music tile, then the card growing out of it frame by frame (30 a second, with the real
    /// timings), turning to the playlist, and another song chosen. Frames are cut to the card's region
    /// (<c>desk-music</c> in the layout); the whole screen is saved closed, open, on the playlist and after the choice.
    /// </summary>
    private static async Task MusicDesk()
    {
        var songs = PromoSongs();
        var covers = songs.Select((s, i) => Cover(s.From, s.To, i)).ToList();
        var list = songs.Select((s, i) => (s.Title, s.Channel, (ImageSource)covers[i])).ToList();
        Desk.Settings = () => Options;
        WallpaperGlass.Refresh();
        var width = SystemParameters.PrimaryScreenWidth;
        var height = SystemParameters.PrimaryScreenHeight;
        Layout["screen"] = new { width, height };
        var clock = new ClockWidget();
        var weather = new WeatherWidget();
        var media = new MediaWidget { Current = Options };
        DockWidget[] widgets = [clock, media, weather];
        foreach (var widget in widgets)
        {
            widget.Current = Options;
            widget.ApplySettings(Options);
        }
        clock.Start();
        weather.Start();
        clock.SetLive(true);
        weather.SetLive(true);
        var popups = new List<WidgetPopup>();
        WidgetPopup.Rehearsal = popups;
        try
        {
            media.RehearsePromo(songs[2].Title, songs[2].Channel, covers[2]);
        }
        finally
        {
            WidgetPopup.Rehearsal = null;
        }
        var canvas = DeskCanvas(width, height);
        var (dock, _) = BuildDock(widgets, 8);
        var (left, top) = PlaceDock(canvas, dock, width, height, true);
        await Task.Delay(7000);
        Arrange(canvas);
        var tile = media.View.TransformToAncestor(canvas).TransformBounds(new Rect(0, 0, media.View.ActualWidth, media.View.ActualHeight));
        Save(canvas, "desk-music-closed.png");
        if (popups.FirstOrDefault() is not { } popup) return;

        // the card in the sheet it grows out of the dock in, placed as opening it there would place it
        var sheet = popup.RehearseSheet();
        var anchor = new EdgePanel.DockAnchor(DockEdge.Bottom, tile.X + tile.Width / 2, tile.Width, top, left + 18, left + dock.ActualWidth - 18);
        var (frame, bounds) = sheet.RehearseOnDock(anchor);
        Canvas.SetLeft(frame, bounds.X);
        Canvas.SetTop(frame, bounds.Y);
        canvas.Children.Add(frame);
        var region = new Rect(Math.Floor(bounds.X - 30), Math.Floor(bounds.Y - 30), Math.Ceiling(bounds.Width + 60), Math.Ceiling(top + dock.ActualHeight + 6 - bounds.Y + 30));
        Layout["desk-music"] = new object[]
        {
            new { kind = "dock", x = left, y = top, w = dock.ActualWidth, h = dock.ActualHeight },
            new { kind = "tile", x = tile.X, y = tile.Y, w = tile.Width, h = tile.Height },
            new { kind = "card", x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height },
            new { kind = "region", x = region.X, y = region.Y, w = region.Width, h = region.Height }
        };

        void Pose(double progress, double content, double rise)
        {
            var seam = sheet.RehearseDockMorph(progress, content, rise);
            dock.OpacityMask = seam is { } r ? EdgePanel.NotchMask(new Size(dock.ActualWidth, dock.ActualHeight), r.Left - left, r.Right - left, EdgePanel.SeamDepth) : null;
            Arrange(canvas);
        }
        static double Part(double value) => Math.Clamp(value, 0, 1);

        // opening: the shape swells in 430 ms (soft overshoot); the content fades in after 170 ms and rises 12 points in 380 ms
        const int open = 18;
        for (var i = 0; i <= open; i++)
        {
            var ms = i * 1000.0 / 30;
            var shape = ms >= 430 ? 1 : Motion.Settle.Ease(ms / 430);
            Pose(shape, Part((ms - 170) / 220), 12 * (1 - Motion.Out.Ease(Part((ms - 170) / 380))));
            SaveRegion(canvas, region, $"music-open-{i:00}.png");
        }
        Save(canvas, "desk-music-front.png");

        // turning to the playlist: folds in 140 ms, the back unfolds in 170 ms
        var fold = new CubicEase { EasingMode = EasingMode.EaseIn };
        var unfold = new CubicEase { EasingMode = EasingMode.EaseOut };
        var turn = 0;
        for (var i = 1; i <= 4; i++)
        {
            media.RehearseTurn(1 - fold.Ease(Part(i * 1000.0 / 30 / 140)));
            Arrange(canvas);
            SaveRegion(canvas, region, $"music-turn-{turn++:00}.png");
        }
        // the list is scrolled before the turn (a card turned edge on cannot be measured)
        media.RehearseTurn(1);
        media.RehearsePlaylist(list, 2, scroll: false);
        for (var pass = 0; pass < 2; pass++)
        {
            Arrange(canvas);
            media.RehearseScrollToCurrent();
            await Task.Delay(150);
        }
        media.RehearseTurn(0);
        Arrange(canvas);
        for (var i = 0; i <= 6; i++)
        {
            media.RehearseTurn(unfold.Ease(Part(i * 1000.0 / 30 / 170)));
            Arrange(canvas);
            SaveRegion(canvas, region, $"music-turn-{turn++:00}.png");
        }
        Layout["music-turn"] = new { frames = turn };
        Save(canvas, "desk-music-back.png");

        // another song chosen: it plays, the tile and the list follow
        media.RehearseNowPlaying(songs[3].Title, songs[3].Channel, covers[3]);
        media.RehearsePlaylist(list, 3, scroll: false);
        Arrange(canvas);
        media.RehearseScrollToCurrent(2);
        await Task.Delay(300);
        Arrange(canvas);
        Save(canvas, "desk-music-next.png");
        foreach (var widget in widgets) widget.Dispose();
    }

    private static void Arrange(Canvas canvas)
    {
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        canvas.UpdateLayout();
    }

    /// <summary>Part of the canvas (DIPs), at the pictures' scale.</summary>
    private static void SaveRegion(Canvas canvas, Rect region, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(canvas.Width * Scale), (int)Math.Ceiling(canvas.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        int x = (int)(region.X * Scale), y = (int)(region.Y * Scale);
        var cut = new CroppedBitmap(bitmap, new Int32Rect(x, y, Math.Min((int)(region.Width * Scale), bitmap.PixelWidth - x), Math.Min((int)(region.Height * Scale), bitmap.PixelHeight - y)));
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(cut));
        using var file = File.Create(System.IO.Path.Combine(_folder, name));
        png.Save(file);
    }

    // ---- Desktop scenes over the promotional wallpaper ------------------------------------------------

    private sealed record Card(string Kind, DeskSize Size, double X, double Y, string? Style = null, string? Tint = null);

    private static async Task Desks()
    {
        Desk.Settings = () => Options;
        WallpaperGlass.Refresh();
        var width = SystemParameters.PrimaryScreenWidth;
        var height = SystemParameters.PrimaryScreenHeight;
        Layout["screen"] = new { width, height };
        var u = Desk.Unit;
        var g = Desk.Gap;
        var scenes = new List<(string Name, Card[] Cards, bool Glass, bool Dock)>
        {
            ("desk-glass", [
                new("bigclock", DeskSize.Hero, 64, 60, "lock", "white"),
                new("weather", DeskSize.Medium, width - 64 - 2 * u - g, 60),
                new("claude", DeskSize.Small, width - 64 - 2 * u - g, 60 + u + g),
                new("cpu", DeskSize.Small, width - 64 - u, 60 + u + g),
                new("devicebattery", DeskSize.Medium, width - 64 - 2 * u - g, 60 + 2 * (u + g))], true, true),
            ("desk-dark", [
                new("bigclock", DeskSize.Hero, 64, 60, "lock", "white"),
                new("weather", DeskSize.Medium, width - 64 - 2 * u - g, 60),
                new("claude", DeskSize.Small, width - 64 - 2 * u - g, 60 + u + g),
                new("cpu", DeskSize.Small, width - 64 - u, 60 + u + g),
                new("devicebattery", DeskSize.Medium, width - 64 - 2 * u - g, 60 + 2 * (u + g))], false, true),
            ("desk-battery", [
                new("devicebattery", DeskSize.Tall, width / 2 - 1.5 * u - g, height / 2 - u - g / 2 - 60),
                new("devicebattery", DeskSize.Medium, width / 2 - 0.5 * u, height / 2 - u - g / 2 - 60),
                new("devicebattery", DeskSize.Strip, width / 2 - 0.5 * u, height / 2 + g / 2 - 60)], true, false),
            ("desk-cpu", [
                new("cpu", DeskSize.Tall, width / 2 - 1.5 * u - g, height / 2 - u - g / 2 - 60),
                new("cpu", DeskSize.Medium, width / 2 - 0.5 * u, height / 2 - u - g / 2 - 60),
                new("cpu", DeskSize.Small, width / 2 - 0.5 * u, height / 2 + g / 2 - 60),
                new("ram", DeskSize.Small, width / 2 + 0.5 * u + g, height / 2 + g / 2 - 60)], true, false),
            ("desk-ai", [
                new("claude", DeskSize.Medium, width / 2 - 2 * u - 1.5 * g, height / 2 - u - g / 2 - 60),
                new("opencode", DeskSize.Tall, width / 2 + g / 2 + u + g, height / 2 - u - g / 2 - 60),
                new("codex", DeskSize.Small, width / 2 - 2 * u - 1.5 * g, height / 2 + g / 2 - 60),
                new("gemini", DeskSize.Small, width / 2 - u - 0.5 * g, height / 2 + g / 2 - 60),
                new("gemini", DeskSize.Medium, width / 2 - 2 * u - 1.5 * g, height / 2 + u + 1.5 * g - 60),
                new("claude", DeskSize.Small, width / 2 + g / 2, height / 2 - u - g / 2 - 60),
                new("codex", DeskSize.Small, width / 2 + g / 2, height / 2 + g / 2 - 60)], true, false)
        };
        foreach (var style in new[] { "lock", "thin", "stacked", "neon", "twotone", "serif" })
        {
            var tint = style switch { "neon" => "pink", "twotone" => "sunset", "serif" => "cream", "thin" => "white", "stacked" => "ocean", _ => "white" };
            scenes.Add(($"desk-bigclock-{style}", [new("bigclock", DeskSize.Hero, width / 2 - (4 * u + 3 * g) / 2, height / 2 - 230 - 70, style, tint)], true, false));
        }
        foreach (var scene in scenes)
        {
            DeskCardFace.GlassEnabled = scene.Glass;
            var canvas = DeskCanvas(width, height);
            var hosts = new List<PromoHost>();
            var rects = new List<object>();
            foreach (var card in scene.Cards)
            {
                if (DeskCatalog.Find(card.Kind) is not { } kind) continue;
                var item = new DeskItem { Kind = card.Kind, Size = card.Size, X = card.X, Y = card.Y };
                if (card.Style is not null) item.Options["style"] = card.Style;
                if (card.Tint is not null) item.Options["tint"] = card.Tint;
                var host = new PromoHost(item, kind);
                hosts.Add(host);
                Canvas.SetLeft(host.Face.Root, item.X);
                Canvas.SetTop(host.Face.Root, item.Y);
                canvas.Children.Add(host.Face.Root);
                var dims = Desk.Dimensions(card.Size);
                rects.Add(new { kind = card.Kind, x = card.X, y = card.Y, w = dims.Width, h = dims.Height });
            }
            if (scene.Dock)
            {
                var (dock, _) = BuildDock([], 10);
                var (left, top) = PlaceDock(canvas, dock, width, height, scene.Glass);
                rects.Add(new { kind = "dock", x = left, y = top, w = dock.DesiredSize.Width, h = dock.DesiredSize.Height });
            }
            Layout[scene.Name] = rects;
            canvas.Measure(new Size(width, height));
            canvas.Arrange(new Rect(0, 0, width, height));
            await Task.Delay(scene.Name == "desk-glass" ? 7000 : 2500);
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var host in hosts) host.Widget.Tick();
                canvas.Measure(new Size(width, height));
                canvas.Arrange(new Rect(0, 0, width, height));
                canvas.UpdateLayout();
            }
            Save(canvas, scene.Name + ".png");
            foreach (var host in hosts) host.Widget.Detach();
        }
        DeskCardFace.GlassEnabled = true;
    }

    /// <summary>A screen-sized canvas showing the wallpaper, in QiDock's font.</summary>
    private static Canvas DeskCanvas(double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height };
        if (WallpaperTint.WallpaperFile() is { } file)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            canvas.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        canvas.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "InterfaceFont");
        TextOptions.SetTextFormattingMode(canvas, TextFormattingMode.Ideal);
        return canvas;
    }

    /// <summary>The dock at the bottom centre of the screen, as glass over the wallpaper when asked.</summary>
    private static (double Left, double Top) PlaceDock(Canvas canvas, Border dock, double width, double height, bool glass)
    {
        dock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = Math.Round((width - dock.DesiredSize.Width) / 2);
        var top = height - 6 - dock.DesiredSize.Height;
        if (glass)
        {
            var area = new Rect(left, top, dock.DesiredSize.Width, dock.DesiredSize.Height);
            GlassLook.DockVeil = GlassLook.Veil(WallpaperGlass.Light(area));
            GlassLook.Tiles(dock.Resources, true);
            if (GlassLook.Dock(new Matrix(1, 0, 0, 1, left, top)) is { } material) dock.Background = material;
            dock.BorderBrush = GlassLook.Rim;
        }
        Canvas.SetLeft(dock, left);
        Canvas.SetTop(dock, top);
        canvas.Children.Add(dock);
        return (left, top);
    }

    private sealed class PromoHost : IDeskHost
    {
        public PromoHost(DeskItem item, DeskKind kind)
        {
            Item = item;
            Widget = kind.Create();
            Widget.Host = this;
            var dims = Desk.Dimensions(item.Size);
            Face.SetSize(dims, Desk.Padding(item.Size), Widget.Bare);
            Face.Place(new Rect(item.X, item.Y, dims.Width, dims.Height));
            Face.Card.Child = Widget.Build();
            Widget.Attach();
        }

        public DeskCardFace Face { get; } = new();
        public DeskItem Item { get; }
        public DeskWidget Widget { get; }
        public bool IsLive => true;
        public void Save() { }
        public void FocusInput(IInputElement element) { }
        public void Rebuild() => Face.Card.Child = Widget.Build();
    }

    // ---- Settings pages ---------------------------------------------------------------------------------

    private static async Task Settings()
    {
        foreach (var (tab, name, wait) in new[] { (1, "settings-look.png", 1500), (3, "settings-wallpaper.png", 10000) })
        {
            var settings = new SettingsWindow(((App)Application.Current).Preferences)
            {
                Left = -30000, Top = 0, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false
            };
            settings.PreviewHost.Visibility = Visibility.Collapsed;
            settings.Show();
            settings.SettingsTabs.SelectedIndex = tab;
            await Task.Delay(wait);
            Save((FrameworkElement)settings.Content, name);
            settings.Close();
        }
    }

    // ---- Files window over a folder of sample files ---------------------------------------------------

    private static async Task Files()
    {
        if (Environment.GetEnvironmentVariable("QIDOCK_PROMO_FILES") is not { Length: > 0 } folder || !Directory.Exists(folder)) return;
        QiDock.Files.FilesWindow.Open(folder);
        var window = Application.Current.Windows.OfType<QiDock.Files.FilesWindow>().FirstOrDefault();
        if (window is null) return;
        window.Left = -30000;
        window.Width = 1180;
        window.Height = 720;
        await Task.Delay(7000);
        Save((FrameworkElement)window.Content, "files.png");
        window.Close();
    }

    // ---- App screen ----------------------------------------------------------------------------------

    private static Task Apps()
    {
        var finished = new TaskCompletionSource();
        AppGridWindow.RenderTo(System.IO.Path.Combine(_folder, "apps.png"), finished.SetResult);
        return finished.Task;
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static Window Stage(FrameworkElement content)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, SizeToContent = SizeToContent.WidthAndHeight, Content = content
        };
        window.Show();
        return window;
    }

    private static Border Pad(FrameworkElement element, double room) => new() { Padding = new Thickness(room), Background = Brushes.Transparent, Child = element };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static void Save(FrameworkElement element, string name)
    {
        if (element is not Canvas)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new Rect(element.DesiredSize));
        }
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * Scale), (int)Math.Ceiling(element.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(System.IO.Path.Combine(_folder, name));
        png.Save(file);
    }
}
#endif
