using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QiDock.DesktopWidgets;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Widgets;

namespace QiDock.Widgets;

/// <summary>Battery charge as a ring with the percent inside (a bolt under it while charging); a click opens the battery's own card.</summary>
public sealed class BatteryWidget : DockWidget
{
    private static readonly System.Windows.Media.Brush Green = Ui.Solid("#FF30D158");
    private static readonly System.Windows.Media.Brush Yellow = Ui.Solid("#FFFFD60A");
    private static readonly System.Windows.Media.Brush Red = Ui.Solid("#FFFF453A");

    private readonly Button _tile;
    private readonly RingGauge _ring;
    private readonly TextBlock _percent;
    private readonly FrameworkElement _bolt;
    private readonly DispatcherTimer _timer;
    private readonly WidgetPopup _popup;
    private BatteryStatus.State? _state;
    private string _last = "";

    public BatteryWidget()
    {
        _ring = new RingGauge { Width = 34, Height = 34, Thickness = 3.5, TrackBrush = Ui.Solid("#47808080"), Stroke = Green };
        _percent = Ui.Text("", 11.5, FontWeights.Bold);
        _percent.HorizontalAlignment = HorizontalAlignment.Center;
        _bolt = Ui.Glyph("GlyphBolt", 7, Green);
        _bolt.HorizontalAlignment = HorizontalAlignment.Center;
        _bolt.Margin = new Thickness(0, -1, 0, 0);
        _bolt.Visibility = Visibility.Collapsed;
        var inside = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        inside.Children.Add(_percent);
        inside.Children.Add(_bolt);
        var face = new Grid { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(_ring);
        face.Children.Add(inside);
        _tile = Ui.Tile(face, (_, _) => TogglePopup(), horizontalPadding: 0);
        _popup = new WidgetPopup(_tile);
        _tile.SetResourceReference(FrameworkElement.WidthProperty, "WidgetTileHeight");
        _tile.Visibility = Visibility.Collapsed;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Update();
        UiText.Current.PropertyChanged += (_, _) => { _last = ""; Update(); };
    }

    public override string Id => WidgetIds.Battery;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    public override void Start()
    {
        Update();
        _timer.Start();
    }

    public override void Stop()
    {
        _timer.Stop();
        _popup.Close();
    }

    public override void SetLive(bool live)
    {
        if (!live)
        {
            _timer.Stop();
            return;
        }
        Update();
        _timer.Start();
    }

    private void Update()
    {
        var state = _state = BatteryStatus.Read();
        var signature = state is null ? "none" : $"{state.Percent}{state.Charging}{state.OnAc}{state.Remaining?.TotalMinutes:0}";
        if (signature == _last) return;
        _last = signature;
        var visible = state is not null;
        if ((_tile.Visibility == Visibility.Visible) != visible)
        {
            _tile.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            RaiseLayoutChanged();
        }
        if (_popup.IsOpen) BuildPopup();
        if (state is null) return;
        var color = state.Percent <= 15 && !state.OnAc ? Red : state.Percent <= 30 && !state.OnAc ? Yellow : Green;
        _ring.Stroke = color;
        _ring.Value = state.Percent;
        _ring.InvalidateVisual();
        _percent.Text = state.Percent.ToString(CultureInfo.InvariantCulture);
        _bolt.Visibility = state.Charging || state.OnAc ? Visibility.Visible : Visibility.Collapsed;
    }

    private static System.Windows.Media.Brush ColorFor(BatteryStatus.State state) =>
        state.Percent <= 15 && !state.OnAc ? Red : state.Percent <= 30 && !state.OnAc ? Yellow : Green;

    private void TogglePopup()
    {
        if (!_popup.IsOpen)
        {
            _last = "";
            Update();
            BuildPopup();
        }
        _popup.Toggle();
    }

    /// <summary>The battery's card: a large ring, where the power comes from, the time left and a way to the settings.</summary>
    private void BuildPopup()
    {
        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = Ui.IconButton(Ui.Icon("IconClose", 12, brushKey: "TextSecondary"), (_, _) => _popup.Close(), UiText.Get("Close"));
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(Ui.Text(UiText.Get("BatteryTitle"), 14, FontWeights.SemiBold));
        root.Children.Add(header);

        if (_state is not { } state)
        {
            root.Children.Add(Ui.Text(UiText.Get("BatteryNone"), 12, brushKey: "TextSecondary"));
            _popup.SetContent(root, 280);
            return;
        }

        var color = ColorFor(state);
        var ring = new RingGauge { Width = 72, Height = 72, Thickness = 6, TrackBrush = Ui.Solid("#47808080"), Stroke = color, Value = state.Percent };
        var percent = Ui.Text(state.Percent.ToString(CultureInfo.InvariantCulture) + "%", 17, FontWeights.Bold);
        percent.HorizontalAlignment = HorizontalAlignment.Center;
        percent.VerticalAlignment = VerticalAlignment.Center;
        var face = new Grid { Width = 72, Height = 72 };
        face.Children.Add(ring);
        face.Children.Add(percent);

        var source = state.Charging ? UiText.Get("BatteryCharging")
            : state.OnAc ? (state.Percent >= 100 ? UiText.Get("BatteryFull") : UiText.Get("BatteryPlugged"))
            : UiText.Get("BatteryOnBattery");
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (state.Charging || state.OnAc)
        {
            var bolt = Ui.Glyph("GlyphBolt", 10, Green);
            bolt.Margin = new Thickness(0, 0, 6, 0);
            bolt.VerticalAlignment = VerticalAlignment.Center;
            sourceRow.Children.Add(bolt);
        }
        sourceRow.Children.Add(Ui.Text(source, 13.5, FontWeights.SemiBold));
        info.Children.Add(sourceRow);
        if (!state.OnAc)
        {
            var left = state.Remaining is { } r ? UiText.Format("BatteryLeft", (int)r.TotalHours, r.Minutes) : UiText.Get("BatteryEstimating");
            var leftText = Ui.Text(left, 12, brushKey: "TextSecondary");
            leftText.Margin = new Thickness(0, 3, 0, 0);
            info.Children.Add(leftText);
        }
        if (state.Saver)
        {
            var saver = Ui.Text(UiText.Get("BatterySaverOn"), 12, FontWeights.SemiBold, brush: Yellow);
            saver.Margin = new Thickness(0, 3, 0, 0);
            info.Children.Add(saver);
        }
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        top.Children.Add(face);
        top.Children.Add(info);
        root.Children.Add(top);

        var settings = new Button { Content = UiText.Get("BatterySettings"), HorizontalAlignment = HorizontalAlignment.Left };
        settings.SetResourceReference(FrameworkElement.StyleProperty, "WidgetPillButton");
        settings.Click += (_, _) =>
        {
            _popup.Close();
            Ui.Open("ms-settings:batterysaver");
        };
        root.Children.Add(settings);
        _popup.SetContent(root, 300);
    }
}
