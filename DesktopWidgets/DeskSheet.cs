#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiDock.Models;

namespace QiDock.DesktopWidgets;

/// <summary>Debug builds only: renders every desktop widget in every size to one PNG for review.</summary>
internal static class DeskSheet
{
    public static void Render(string path, Func<WidgetSettings> settings, Action done)
    {
        Desk.Settings = settings;
        // QIDOCK_SHEET_DESKTOP=1 draws the user's own desktop instead: wallpaper and widgets where they are.
        if (Environment.GetEnvironmentVariable("QIDOCK_SHEET_DESKTOP") == "1")
        {
            RenderDesktop(path, done);
            return;
        }
        var panel = new WrapPanel { Width = 2300, Background = QiDock.Widgets.Ui.Vertical("#FF5B86C5", "#FF2A4C8C") };
        panel.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "InterfaceFont");
        TextOptions.SetTextFormattingMode(panel, TextFormattingMode.Ideal);
        var hosts = new List<SheetHost>();
        // QIDOCK_SHEET_ONLY=bigclock,devicebattery renders just those kinds.
        var only = Environment.GetEnvironmentVariable("QIDOCK_SHEET_ONLY")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var kind in DeskCatalog.All.Where(k => only is not { Length: > 0 } || only.Contains(k.Id)))
        foreach (var (size, style) in kind.Id == "bigclock"
            ? Kinds.BigClockWidget.Styles.Select(style => (DeskSize.Hero, (string?)style.Id))
            : kind.Sizes.Select(size => (size, (string?)null)))
        {
            // QIDOCK_SHEET_SIZE=Tall renders only that size.
            if (Environment.GetEnvironmentVariable("QIDOCK_SHEET_SIZE") is { Length: > 0 } onlySize && size.ToString() != onlySize) continue;
            var host = new SheetHost(kind, size, style);
            hosts.Add(host);
            var label = DeskUi.T($"{kind.Id} / {size} {style}", 13, FontWeights.SemiBold, Brushes.White);
            var cell = new StackPanel { Margin = new Thickness(14) };
            cell.Children.Add(label);
            cell.Children.Add(DeskUi.Margin(host.Card, 0, 6));
            panel.Children.Add(cell);
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var host in hosts) host.Widget.Tick();
                panel.Measure(new Size(panel.Width, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                panel.UpdateLayout();
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            foreach (var host in hosts) host.Widget.Detach();
            done();
        };
        panel.Measure(new Size(panel.Width, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        timer.Start();
    }

    private static void RenderDesktop(string path, Action done)
    {
        Services.Widgets.WallpaperGlass.Refresh();
        DeskCardFace.GlassEnabled = Environment.GetEnvironmentVariable("QIDOCK_SHEET_GLASS") != "0";
        var width = SystemParameters.PrimaryScreenWidth;
        var height = SystemParameters.PrimaryScreenHeight;
        var canvas = new Canvas { Width = width, Height = height, Background = Brushes.Black };
        if (Services.Widgets.WallpaperTint.WallpaperFile() is { } file)
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
        var hosts = new List<DesktopHost>();
        foreach (var item in DeskStore.Load().Items)
        {
            if (DeskCatalog.Find(item.Kind) is not { } kind) continue;
            var host = new DesktopHost(item, kind);
            hosts.Add(host);
            Canvas.SetLeft(host.Face.Root, item.X);
            Canvas.SetTop(host.Face.Root, item.Y);
            canvas.Children.Add(host.Face.Root);
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var host in hosts) host.Widget.Tick();
            canvas.Measure(new Size(width, height));
            canvas.Arrange(new Rect(0, 0, width, height));
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width * 2, (int)height * 2, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            foreach (var host in hosts) host.Widget.Detach();
            done();
        };
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        timer.Start();
    }

    private sealed class DesktopHost : IDeskHost
    {
        public DesktopHost(DeskItem item, DeskKind kind)
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

    private sealed class SheetHost : IDeskHost
    {
        private static int _count;

        public SheetHost(DeskKind kind, DeskSize size, string? style = null)
        {
            Item = new DeskItem { Kind = kind.Id, Size = size };
            if (style is not null) Item.Options["style"] = style;
            // QIDOCK_SHEET_LANG=de writes the big clocks' dates in that language.
            if (style is not null && Environment.GetEnvironmentVariable("QIDOCK_SHEET_LANG") is { Length: > 0 } lang) Item.Options["lang"] = lang;
            // QIDOCK_SHEET_TINT=cycle gives each big clock a different colour from the palette.
            if (style is not null && Environment.GetEnvironmentVariable("QIDOCK_SHEET_TINT") == "cycle")
            {
                var palette = Kinds.BigClockWidget.Palette;
                Item.Options["tint"] = palette[(_count++ + 2) % palette.Length].Id;
            }
            Widget = kind.Create();
            Widget.Host = this;
            var dims = Desk.Dimensions(size);
            Card = new Border
            {
                Width = dims.Width,
                Height = dims.Height,
                CornerRadius = new CornerRadius(24),
                Background = DeskUi.CardFill,
                BorderBrush = DeskUi.CardStroke,
                BorderThickness = new Thickness(1),
                Padding = Desk.Padding(size),
                Child = Widget.Build()
            };
            Card.Clip = new RectangleGeometry(new Rect(0, 0, dims.Width, dims.Height), 24, 24);
            if (Widget.Bare)
            {
                Card.Background = Brushes.Transparent;
                Card.BorderBrush = Brushes.Transparent;
                Card.Padding = new Thickness(0);
            }
            Widget.Attach();
        }

        public DeskItem Item { get; }
        public DeskWidget Widget { get; }
        public Border Card { get; }
        public bool IsLive => true;
        public void Save() { }
        public void FocusInput(IInputElement element) { }
        public void Rebuild() => Card.Child = Widget.Build();
    }
}
#endif
