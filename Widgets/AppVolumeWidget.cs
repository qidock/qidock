using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Widgets;

namespace QiDock.Widgets;

/// <summary>Per-app volume: one slider and mute button for every app that has sound open.</summary>
public sealed class AppVolumeWidget : DockWidget
{
    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private AudioSessions? _sessions;

    public AppVolumeWidget()
    {
        _tile = Ui.Tile(Ui.Icon("IconMixer", 18), (_, _) => Toggle(), 12);
        _tile.ToolTip = UiText.Get("AppVolumeTitle");
        _popup = new WidgetPopup(_tile);
        _popup.Closed += (_, _) =>
        {
            _sessions?.Dispose();
            _sessions = null;
        };
    }

    public override string Id => WidgetIds.AppVolume;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();
    public override void Stop() => _popup.Close();

    private void Toggle()
    {
        if (!_popup.IsOpen) Build();
        _popup.Toggle();
    }

    private void Build()
    {
        _sessions?.Dispose();
        _sessions = new AudioSessions();
        var apps = _sessions.Read();
        var root = new StackPanel();
        root.Children.Add(Ui.Header(UiText.Get("AppVolumeTitle"), () => _popup.Close(),
            Ui.IconButton(Ui.Icon("IconRefresh", 13), (_, _) => Build(), UiText.Get("Refresh"))));
        if (apps.Count == 0)
        {
            var empty = Ui.Text(UiText.Get("AppVolumeEmpty"), 12, brushKey: "TextSecondary");
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(0, 0, 0, 6);
            root.Children.Add(empty);
        }
        var list = new StackPanel();
        foreach (var app in apps) list.Children.Add(Row(app));
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _popup.SetContent(root, 320);
    }

    private static FrameworkElement Row(AudioSessions.App app)
    {
        var icon = app.ExecutablePath is { } path ? Ui.FileIcon(path, 22) : Ui.Icon("IconVolume", 16);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Ui.Text(app.Name, 12, FontWeights.SemiBold));
        if (app.Playing)
        {
            var playing = Ui.Text(UiText.Get("AppVolumePlaying"), 10, brushKey: "TextSecondary");
            playing.Margin = new Thickness(6, 1, 0, 0);
            title.Children.Add(playing);
        }
        var percent = Ui.Text("", 10.5, brushKey: "TextSecondary");
        percent.Width = 32;
        percent.TextAlignment = TextAlignment.Right;
        var slider = new Slider { Value = Math.Round(app.Volume * 100), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        slider.SetResourceReference(FrameworkElement.StyleProperty, "WidgetSlider");
        var mute = new Button();
        void Refresh()
        {
            var muted = app.Muted;
            mute.Content = Ui.Icon(muted ? "IconVolumeMute" : "IconVolume", 13, brushKey: muted ? "TextSecondary" : "TextPrimary");
            percent.Text = "%" + (int)slider.Value;
            slider.Opacity = muted ? 0.45 : 1;
        }
        mute = Ui.IconButton(new ContentControl(), (_, _) =>
        {
            app.Muted = !app.Muted;
            Refresh();
        }, UiText.Get("AppVolumeMute"));
        slider.ValueChanged += (_, e) =>
        {
            app.Volume = (float)(e.NewValue / 100);
            if (app.Muted && e.NewValue > 0) app.Muted = false;
            Refresh();
        };
        Refresh();
        // The slider takes the room left, so the percentages end at the card's right edge, under the ×.
        var controls = new DockPanel { Margin = new Thickness(-4, 2, 0, 0) };
        DockPanel.SetDock(mute, Dock.Left);
        DockPanel.SetDock(percent, Dock.Right);
        percent.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Add(mute);
        controls.Children.Add(percent);
        controls.Children.Add(slider);
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(controls);
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }
}
