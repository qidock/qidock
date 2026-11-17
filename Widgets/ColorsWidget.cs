using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// Color picker: pick any pixel on screen with a magnifying loupe; picked colors are kept as
/// swatches, and clicking one copies its hex code.
/// </summary>
public sealed class ColorsWidget : DockWidget
{
    private const int MaxColors = 24; // shared with the desktop color widget
    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly StackPanel _recent = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<string> _colors = [];

    public ColorsWidget()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var pick = Ui.IconButton(Ui.Icon("IconPipette", 14), (_, e) =>
        {
            e.Handled = true; // the tile itself would open the card
            Pick();
        }, UiText.Get("ColorsPick"), 30, 30);
        row.Children.Add(pick);
        row.Children.Add(_recent);
        _tile = Ui.Tile(row, (_, _) => Toggle(), 6);
        _popup = new WidgetPopup(_tile);
    }

    public override string Id => WidgetIds.Colors;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();
    public override void Stop() => _popup.Close();

    public override void ApplySettings(WidgetSettings settings)
    {
        var colors = settings.Colors ?? [];
        if (colors.SequenceEqual(_colors)) return;
        _colors = colors;
        _recent.Children.Clear();
        foreach (var hex in _colors.Take(3))
            _recent.Children.Add(new Border { Width = 16, Height = 26, CornerRadius = new CornerRadius(5), Margin = new Thickness(2, 0, 0, 0), Background = Brush(hex), BorderBrush = Ui.Solid("#40FFFFFF"), BorderThickness = new Thickness(1) });
        if (_popup.IsOpen) Build();
    }

    private static Brush Brush(string hex)
    {
        try { return Ui.Solid(hex); }
        catch (FormatException) { return Brushes.Transparent; }
    }

    private void Toggle()
    {
        if (!_popup.IsOpen) Build();
        _popup.Toggle();
    }

    private void Build()
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header(UiText.Get("ColorsTitle"), () => _popup.Close(),
            Ui.Pill(UiText.Get("ColorsPick"), (_, _) => Pick(), "IconPipette")));
        if (_colors.Count == 0)
        {
            var empty = Ui.Text(UiText.Get("ColorsEmpty"), 12, brushKey: "TextSecondary");
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(2, 0, 0, 8);
            root.Children.Add(empty);
        }
        var grid = new WrapPanel();
        foreach (var hex in _colors)
        {
            var label = Ui.Text(hex, 10.5, FontWeights.SemiBold, brush: Contrast(hex));
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Bottom;
            label.Margin = new Thickness(8, 0, 0, 6);
            var face = new Border { Width = 92, Height = 58, CornerRadius = new CornerRadius(10), Background = Brush(hex), Child = label, BorderBrush = Ui.Solid("#30FFFFFF"), BorderThickness = new Thickness(1) };
            var swatch = new Button { Content = face, Margin = new Thickness(3), Cursor = Cursors.Hand, ToolTip = UiText.Get("ColorsCopyHint") };
            swatch.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIconButton");
            swatch.Width = 96;
            swatch.Height = 62;
            swatch.Click += (_, _) => Copy(hex);
            swatch.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                UpdateSettings(s => s with { Colors = (s.Colors ?? []).Where(c => c != hex).ToList() });
            };
            grid.Children.Add(swatch);
        }
        root.Children.Add(grid);
        if (_colors.Count > 0)
        {
            var clear = Ui.Pill(UiText.Get("ColorsClear"), (_, _) => UpdateSettings(s => s with { Colors = [] }));
            clear.Margin = new Thickness(3, 8, 0, 0);
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(clear);
        }
        _popup.SetContent(root, 440);
    }

    private static Brush Contrast(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 150 ? Ui.Solid("#CC000000") : Ui.Solid("#E6FFFFFF");
        }
        catch (FormatException) { return Ui.White; }
    }

    private static void Copy(string hex)
    {
        try
        {
            Clipboard.SetText(hex);
            Ui.ShowHud("IconCopy", UiText.Format("ColorsCopied", hex));
        }
        catch (COMException) { }
    }

    private void Pick()
    {
        _popup.Close();
        var overlay = new ColorPickerOverlay();
        overlay.Picked += hex =>
        {
            Copy(hex);
            UpdateSettings(s => s with { Colors = new[] { hex }.Concat((s.Colors ?? []).Where(c => c != hex)).Take(MaxColors).ToList() });
        };
        overlay.Show();
        overlay.Activate();
    }
}

/// <summary>Full-screen, nearly invisible window with a loupe that follows the pointer; click picks.</summary>
internal sealed class ColorPickerOverlay : Window
{
    private const int Grab = 11;
    private const double LoupeSize = 120;
    private readonly Image _zoom = new() { Width = LoupeSize, Height = LoupeSize, Stretch = Stretch.Fill };
    private readonly Canvas _canvas = new();
    private readonly Border _loupe;
    private readonly TextBlock _hex;
    private string _current = "#000000";

    public event Action<string>? Picked;

    public ColorPickerOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);

        var lens = new Grid { Width = LoupeSize, Height = LoupeSize, Clip = new EllipseGeometry(new Point(LoupeSize / 2, LoupeSize / 2), LoupeSize / 2, LoupeSize / 2) };
        lens.Children.Add(_zoom);
        var cell = LoupeSize / Grab;
        lens.Children.Add(new Rectangle { Width = cell, Height = cell, Stroke = Brushes.White, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var ring = new Ellipse { Width = LoupeSize, Height = LoupeSize, Stroke = Brushes.White, StrokeThickness = 3 };
        lens.Children.Add(ring);
        _hex = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var label = new Border { Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1C, 0x1E, 0x24)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 4), Child = _hex, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(lens);
        stack.Children.Add(label);
        _loupe = new Border { Child = stack, IsHitTestVisible = false, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45 } };
        _canvas.Children.Add(_loupe);
        Content = _canvas;

        MouseMove += (_, _) => Track();
        MouseLeftButtonUp += (_, _) =>
        {
            Track();
            var hex = _current;
            Close();
            Picked?.Invoke(hex);
        };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Loaded += (_, _) => Track();
    }

    private void Track()
    {
        if (!GetCursorPos(out var cursor)) return;
        var bitmap = Capture(cursor.X - Grab / 2, cursor.Y - Grab / 2, Grab, Grab);
        if (bitmap is not null)
        {
            _zoom.Source = bitmap;
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(Grab / 2, Grab / 2, 1, 1), pixel, 4, 0);
            _current = $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";
            _hex.Text = _current;
        }
        var point = PointFromScreen(new Point(cursor.X, cursor.Y));
        var x = point.X + 24;
        var y = point.Y + 24;
        if (x + LoupeSize > ActualWidth) x = point.X - 24 - LoupeSize;
        if (y + LoupeSize + 40 > ActualHeight) y = point.Y - 24 - LoupeSize - 40;
        Canvas.SetLeft(_loupe, x);
        Canvas.SetTop(_loupe, y);
    }

    /// <summary>Screen pixels without layered windows (so this overlay and its loupe never show up).</summary>
    private static BitmapSource? Capture(int x, int y, int width, int height)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020 /* SRCCOPY, no CAPTUREBLT */)) return null;
            SelectObject(memory, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
