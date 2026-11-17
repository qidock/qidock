using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// Voice memo: one click starts recording from the default microphone, the next saves it as a .wav file in
/// Documents\Ses Kayıtları. The card lists the latest recordings (click to play) and opens the folder.
/// Recording uses Windows' own sound recorder (MCI), so nothing extra is installed.
/// </summary>
public sealed class VoiceMemoWidget : DockWidget
{
    private const string Alias = "qidockmemo";
    private static readonly System.Windows.Media.Brush Red = Ui.Solid("#FFFF453A");

    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly ContentControl _icon = new() { Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _title;
    private readonly TextBlock _caption;
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _startedAt;
    private bool _recording;
    private string? _last;

    public VoiceMemoWidget()
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title = Ui.Text("", 12, FontWeights.SemiBold);
        _title.FontFeatures();
        _caption = Ui.Text("", 10, brushKey: "TextSecondary");
        text.Children.Add(_title);
        text.Children.Add(_caption);
        var record = Ui.IconButton(_icon, (_, e) =>
        {
            e.Handled = true;
            if (_recording) Finish();
            else Begin();
        }, UiText.Get("MemoRecordStop"), 30, 30);
        _icon.Margin = new Thickness(0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        record.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(record);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (_, _) => Toggle(), 7);
        _popup = new WidgetPopup(_tile);
        _clock.Tick += (_, _) => Render();
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.VoiceMemo;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    public override void Stop()
    {
        // Hidden while recording: keep what was said.
        if (_recording) Finish();
        _popup.Close();
    }

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ses Kayıtları");

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? result, int length, nint callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorString(int error, StringBuilder text, int length);

    private static bool Mci(string command)
    {
        var error = mciSendString(command, null, 0, 0);
        if (error == 0) return true;
        var text = new StringBuilder(256);
        mciGetErrorString(error, text, text.Capacity);
        AppLog.Write($"Ses kaydı ({command.Split(' ')[0]}): {text}");
        return false;
    }

    private void Begin()
    {
        Mci("close " + Alias); // a recording left open by a crash
        // 16-bit mono 44.1 kHz: clear speech in about 5 MB a minute.
        if (!Mci($"open new type waveaudio alias {Alias}")
            || !Mci($"set {Alias} bitspersample 16 channels 1 samplespersec 44100 bytespersec 88200 alignment 2")
            || !Mci($"record {Alias}"))
        {
            Mci("close " + Alias);
            Ui.ShowHud("IconMic", UiText.Get("MemoNoMic"));
            return;
        }
        _recording = true;
        _startedAt = DateTime.Now;
        _clock.Start();
        Render();
    }

    private void Finish()
    {
        _clock.Stop();
        _recording = false;
        Mci("stop " + Alias);
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"Kayıt {_startedAt:yyyy-MM-dd HH-mm-ss}.wav");
            if (Mci($"save {Alias} \"{path}\""))
            {
                _last = path;
                Ui.ShowHud("IconMic", UiText.Format("MemoSaved", (DateTime.Now - _startedAt).ToString(@"m\:ss")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Ses kaydı kaydedilemedi: " + ex.Message);
        }
        finally
        {
            Mci("close " + Alias);
        }
        Render();
        if (_popup.IsOpen) Build();
    }

    private void Render()
    {
        if (_recording)
        {
            var dot = new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, Fill = Red };
            if (_icon.Content is not System.Windows.Shapes.Rectangle)
            {
                // A slow pulse says the microphone is on.
                dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                _icon.Content = dot;
            }
            var elapsed = DateTime.Now - _startedAt;
            _title.Text = elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
            _title.Foreground = Red;
            _caption.Text = UiText.Get("MemoRecording");
        }
        else
        {
            _icon.Content = new System.Windows.Shapes.Ellipse { Width = 13, Height = 13, Fill = Red };
            _title.Text = UiText.Get("MemoTitle");
            _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            _caption.Text = UiText.Get("MemoTapToRecord");
        }
        _tile.ToolTip = UiText.Get("MemoHint");
    }

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.Close();
            return;
        }
        Build();
        _popup.Toggle();
    }

    private void Build()
    {
        var root = new StackPanel();
        var folder = Ui.IconButton(Ui.Icon("IconFolderOpen", 13), (_, _) =>
        {
            Directory.CreateDirectory(Folder);
            Ui.Open(Folder);
        }, UiText.Get("MemoOpenFolder"));
        root.Children.Add(Ui.Header(UiText.Get("MemoTitle"), () => _popup.Close(), folder));
        var files = Directory.Exists(Folder)
            ? new DirectoryInfo(Folder).EnumerateFiles("*.wav").OrderByDescending(f => f.CreationTime).Take(6).ToList()
            : [];
        if (files.Count == 0)
        {
            var none = Ui.Text(UiText.Get("MemoNone"), 11.5, brushKey: "TextSecondary");
            none.TextWrapping = TextWrapping.Wrap;
            root.Children.Add(none);
        }
        foreach (var file in files)
        {
            var row = new DockPanel();
            var play = Ui.Glyph("GlyphPlay", 10);
            play.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(play, Dock.Left);
            var size = Ui.Text($"{file.Length / 1024.0 / 1024.0:0.0} MB", 10.5, brushKey: "TextSecondary");
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(play);
            row.Children.Add(size);
            row.Children.Add(Ui.Text(Path.GetFileNameWithoutExtension(file.Name), 12, file.FullName == _last ? FontWeights.SemiBold : FontWeights.Normal));
            var open = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 5, 6, 5) };
            open.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIconButton");
            open.Width = double.NaN;
            open.Height = double.NaN;
            open.HorizontalAlignment = HorizontalAlignment.Stretch;
            var path = file.FullName;
            open.Click += (_, _) => Ui.Open(path);
            root.Children.Add(open);
        }
        _popup.SetContent(root, 320);
    }
}
