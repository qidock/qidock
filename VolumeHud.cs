using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// Small glass pill that pops in at the top of the screen when the volume changes (volume keys,
/// mute key) and is gone again a moment later. Click-through and never focused.
/// </summary>
public sealed class VolumeHud : Window
{
    private const double Shadow = 16;
    private const double BarWidth = 150;
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(1100);

    private readonly Border _surface;
    private readonly ScaleTransform _scale = new(0.86, 0.86);
    private readonly ContentControl _icon = new() { Width = 18, Height = 18, Focusable = false };
    private readonly Border _fill;
    private readonly TextBlock _percent;
    private readonly Border _track;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _hideTimer;
    private bool _shown;

    /// <summary>Screen-top offset in DIP (below the widget bar when that is on screen).</summary>
    public Func<double> TopOffset { get; set; } = () => 10;

    public VolumeHud()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "QiDock Volume";
        SetResourceReference(FontFamilyProperty, "InterfaceFont");

        var track = _track = new Border { Width = BarWidth, Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Solid("#33FFFFFF"), VerticalAlignment = VerticalAlignment.Center };
        _fill = new Border { Width = 0, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Solid("#FFFFFFFF") };
        track.Child = _fill;
        _percent = Ui.Text("", 12.5, FontWeights.SemiBold);
        _percent.Width = 34;
        _percent.TextAlignment = TextAlignment.Right;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        track.Margin = new Thickness(10, 0, 8, 0);
        row.Children.Add(track);
        row.Children.Add(_percent);
        _message = Ui.Text("", 12.5, FontWeights.SemiBold);
        _message.Margin = new Thickness(10, 0, 2, 0);
        _message.Visibility = Visibility.Collapsed;
        row.Children.Add(_message);

        _surface = new Border
        {
            Margin = new Thickness(Shadow),
            Padding = new Thickness(14, 9, 14, 9),
            CornerRadius = new CornerRadius(19),
            BorderThickness = new Thickness(1),
            Child = row,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 2, Direction = 270, Opacity = 0.32, RenderingBias = RenderingBias.Performance }
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "MenuMaterial");
        _surface.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        Content = _surface;
        SizeChanged += (_, _) => Reposition();

        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = Linger };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Disappear();
        };
    }

    public void ShowLevel(float level, bool muted)
    {
        var percent = (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);
        _icon.Content = Ui.Icon(muted || percent == 0 ? "IconVolumeMute" : "IconVolume", 18);
        _fill.Width = muted ? 0 : BarWidth * percent / 100.0;
        _fill.Opacity = muted ? 0.4 : 1;
        _percent.Text = muted ? "" : percent.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _track.Visibility = Visibility.Visible;
        _message.Visibility = Visibility.Collapsed;
        _percent.Visibility = Visibility.Visible;
        Pop(Linger);
    }

    /// <summary>Icon and a short sentence ("Kopyalandı", "Odak süresi bitti").</summary>
    public void ShowMessage(string iconKey, string text)
    {
        _icon.Content = Ui.Icon(iconKey, 18);
        _message.Text = text;
        _track.Visibility = Visibility.Collapsed;
        _percent.Visibility = Visibility.Collapsed;
        _message.Visibility = Visibility.Visible;
        Pop(TimeSpan.FromMilliseconds(2200));
    }

    private void Pop(TimeSpan linger)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = linger;
        _hideTimer.Start();
        if (_shown) return;
        _shown = true;
        if (!IsVisible) Show();
        Reposition();
        var duration = TimeSpan.FromMilliseconds(150);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        _surface.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
    }

    private void Disappear()
    {
        _shown = false;
        var duration = TimeSpan.FromMilliseconds(180);
        var fade = new DoubleAnimation(0, duration);
        fade.Completed += (_, _) =>
        {
            if (!_shown) Hide();
        };
        _surface.BeginAnimation(OpacityProperty, fade);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, duration));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, duration));
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        if (ActualWidth <= 0) return;
        Left = Math.Round(area.Left + (area.Width - ActualWidth) / 2);
        Top = Math.Round(area.Top + TopOffset() - Shadow);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        // Tool window, never activated, transparent to the mouse.
        var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
        SetWindowLongPtr(source.Handle, -20, new nint((style | 0x00000080 | 0x08000000 | 0x00000020) & ~0x00040000));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
