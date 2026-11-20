using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// A small status card that grows out of the middle of the dock the way the bell's list does: a level ("Ses ▬▬ 57",
/// "Parlaklık ▬▬ 75") or a short message, sinking back a moment later.
/// </summary>
public sealed class DockHud : EdgePanel
{
    private const double BarWidth = 64;
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(1400);

    private static DockHud? _shared;
    public static DockHud Shared => _shared ??= new DockHud();
    public static bool IsCreated => _shared is not null;

    private readonly ContentControl _icon = new() { Width = 20, Height = 20, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = Ui.Text("", 14, FontWeights.SemiBold);
    private readonly Border _track;
    private readonly Border _fill;
    private readonly TextBlock _value = Ui.Text("", 13.5, FontWeights.SemiBold, brushKey: "TextSecondary");
    private readonly Border _body;
    private readonly DispatcherTimer _hide;

    private DockHud() : base(270, "QiDock HUD")
    {
        _label.Margin = new Thickness(12, 0, 0, 0);
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.TextTrimming = TextTrimming.CharacterEllipsis;
        _fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        _track = new Border { Width = BarWidth, Height = 6, CornerRadius = new CornerRadius(3), Child = _fill, VerticalAlignment = VerticalAlignment.Center };
        _track.SetResourceReference(Border.BackgroundProperty, "WidgetTrack");
        _value.Width = 34;
        _value.TextAlignment = TextAlignment.Right;
        _value.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { LastChildFill = true, Height = 34 };
        DockPanel.SetDock(_icon, Dock.Left);
        DockPanel.SetDock(_value, Dock.Right);
        DockPanel.SetDock(_track, Dock.Right);
        row.Children.Add(_icon);
        row.Children.Add(_value);
        row.Children.Add(_track);
        row.Children.Add(_label);

        _body = new Border { Padding = new Thickness(10, 2, 8, 2), Width = CardWidth, Child = row };
        _body.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");

        _hide = new DispatcherTimer(DispatcherPriority.Normal) { Interval = Linger };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Close(animated: true);
        };
    }

    /// <summary>Volume, brightness…: a label, a short bar in <paramref name="accent"/> and the value.</summary>
    public void ShowLevel(DockAnchor anchor, string iconKey, string label, int percent, Brush accent, bool dimmed = false)
    {
        SetLevel(iconKey, label, percent, accent, dimmed);
        Present(anchor, Linger);
    }

#if DEBUG
    internal void RehearseLevel(string iconKey, string label, int percent, Brush accent) => SetLevel(iconKey, label, percent, accent, false);

    internal void RehearseMessage(string iconKey, string text)
    {
        _icon.Content = Ui.Icon(iconKey, 20);
        _label.Text = text;
        _track.Visibility = _value.Visibility = Visibility.Collapsed;
    }
#endif

    private void SetLevel(string iconKey, string label, int percent, Brush accent, bool dimmed)
    {
        _icon.Content = Ui.Icon(iconKey, 20);
        _label.Text = label;
        _fill.Width = BarWidth * Math.Clamp(percent, 0, 100) / 100.0;
        _fill.Background = accent;
        _fill.Opacity = dimmed ? 0.35 : 1;
        _value.Text = dimmed ? "" : percent.ToString(CultureInfo.CurrentCulture);
        _track.Visibility = _value.Visibility = Visibility.Visible;
    }

    /// <summary>An icon and a short sentence ("Kopyalandı", "Odak süresi bitti").</summary>
    public void ShowMessage(DockAnchor anchor, string iconKey, string text)
    {
        _icon.Content = Ui.Icon(iconKey, 20);
        _label.Text = text;
        _track.Visibility = _value.Visibility = Visibility.Collapsed;
        Present(anchor, TimeSpan.FromMilliseconds(2200));
    }

    private void Present(DockAnchor anchor, TimeSpan linger)
    {
        _hide.Stop();
        _hide.Interval = linger;
        if (!IsOpen) ToggleFromDock(anchor);
        _hide.Start();
    }

    protected override void Build()
    {
        if (_body.Parent is Panel old) old.Children.Remove(_body);
        Cards.Children.Clear();
        Cards.Children.Add(_body);
    }

    protected override void OnHidden() => _hide.Stop();
}
