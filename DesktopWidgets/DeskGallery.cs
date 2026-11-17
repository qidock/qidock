using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace QiDock.DesktopWidgets;

/// <summary>
/// Gallery of desktop widgets with live previews; pick a size and add it to the desktop.
/// It is a plain (not see-through) window with Windows' own rounded corners: a see-through window this large is
/// drawn whole on every frame, which in low-memory mode (drawing on the processor) froze scrolling to a few frames a
/// second. Cards are made a few at a time after the window shows, and only the previews in view tick.
/// </summary>
internal sealed class DeskGallery : Window
{
    private readonly DeskWidgetManager _manager;
    private readonly WrapPanel _cards = new() { Margin = new Thickness(18, 4, 6, 18) };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 24, 10) };
    private readonly List<PreviewHost> _previews = [];
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private DeskCategory? _category;

    public DeskGallery(DeskWidgetManager manager)
    {
        _manager = manager;
        Title = Desk.L("Widget galerisi", "Widget gallery");
        Width = 1080;
        Height = Math.Min(960, SystemParameters.WorkArea.Height - 80);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Ui("#FF18181B");
        // No title bar, but Windows still draws the frame: rounded corners, a thin border and resizing at the edges.
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        SourceInitialized += (_, _) => RoundCorners();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(FontFamilyProperty, "InterfaceFont");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var title = DeskUi.T(Title, 22, FontWeights.SemiBold);
        var subtitle = DeskUi.T(Desk.L(
            "Bir widget ve boyut seçip masaüstüne ekleyin. Masaüstünde sürükleyerek taşıyın, sağ tıklayarak boyutunu değiştirin ya da kaldırın.",
            "Pick a widget and a size to add it to the desktop. Drag widgets to move them; right-click to resize or remove."), 12.5, brush: DeskUi.Secondary);
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 4, 0, 0);
        var close = DeskButton.Round("IconClose", Close, 32, DeskUi.Inset, outline: true, glyph: 13);
        var header = DeskUi.Grid(columns: "*,Auto");
        header.Margin = new Thickness(24, 20, 18, 14);
        header.Children.Add(DeskUi.Column(title, subtitle));
        header.Children.Add(DeskUi.At(close, 0, 1));
        close.VerticalAlignment = VerticalAlignment.Top;
        header.Background = Brushes.Transparent;
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        AddChip(null, Desk.L("Tümü", "All"));
        AddChip(DeskCategory.Time, Desk.L("Saat ve tarih", "Time & date"));
        AddChip(DeskCategory.Weather, Desk.L("Hava", "Weather"));
        AddChip(DeskCategory.System, Desk.L("Sistem", "System"));
        AddChip(DeskCategory.Media, Desk.L("Medya", "Media"));
        AddChip(DeskCategory.Productivity, Desk.L("Verimlilik", "Productivity"));
        AddChip(DeskCategory.Ai, Desk.L("Yapay zekâ", "AI"));
        AddChip(DeskCategory.Tools, Desk.L("Araçlar", "Tools"));

        var scroll = _scroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var layout = DeskUi.Grid("Auto,Auto,*");
        layout.Children.Add(header);
        layout.Children.Add(DeskUi.At(_chips, 1));
        layout.Children.Add(DeskUi.At(scroll, 2));

        Content = layout;
        UseLayoutRounding = true;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        // Only the previews in view move their hands and numbers; the rest wait until they are scrolled to.
        _tick.Tick += (_, _) =>
        {
            foreach (var preview in _previews)
                if (InView(preview.Card)) preview.Widget.Tick();
        };
        Loaded += (_, _) => Fill();
        Closed += (_, _) => Clear();
    }

    private static Brush Ui(string hex) => QiDock.Widgets.Ui.Solid(hex);

    private ScrollViewer? _scroll;
    private int _fillVersion;

    private bool InView(FrameworkElement card)
    {
        if (_scroll is null || !card.IsVisible || !card.IsDescendantOf(_scroll)) return false;
        var box = card.TransformToAncestor(_scroll).TransformBounds(new Rect(card.RenderSize));
        return box.Bottom > 0 && box.Top < _scroll.ViewportHeight;
    }

    /// <summary>Windows 11 rounds the window's corners and draws a dark hairline around it.</summary>
    private void RoundCorners()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        var border = 0x003A3A3F; // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(handle, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
        var dark = 1;
        DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private void AddChip(DeskCategory? category, string text)
    {
        var label = DeskUi.T(text, 12.5, FontWeights.SemiBold);
        label.Margin = new Thickness(14, 0, 14, 0);
        var chip = new DeskButton(label, () =>
        {
            _category = category;
            Fill();
        }, background: DeskUi.Inset, radius: 15) { Height = 30, Margin = new Thickness(0, 0, 8, 0), Tag = category };
        _chips.Children.Add(chip);
    }

    public void SettingsChanged()
    {
        foreach (var preview in _previews) preview.Widget.SettingsChanged();
    }

    /// <summary>The first row of cards shows at once; the others follow a few at a time, so the window never hangs.</summary>
    private void Fill()
    {
        Clear();
        var version = ++_fillVersion;
        foreach (DeskButton chip in _chips.Children)
            chip.Background = Equals(chip.Tag, _category) ? DeskUi.Blue : DeskUi.Inset;
        var kinds = new Queue<DeskKind>(DeskCatalog.All.Where(k => _category is null || k.Category == _category));
        void Next(int count)
        {
            if (version != _fillVersion) return;
            for (var i = 0; i < count && kinds.Count > 0; i++) _cards.Children.Add(Card(kinds.Dequeue()));
            if (kinds.Count > 0) Dispatcher.BeginInvoke(() => Next(3), DispatcherPriority.Background);
        }
        Next(6);
        _tick.Start();
    }

    private void Clear()
    {
        _fillVersion++; // cards still queued from an earlier fill are no longer made
        _tick.Stop();
        foreach (var preview in _previews) preview.Dispose();
        _previews.Clear();
        _cards.Children.Clear();
    }

    private FrameworkElement Card(DeskKind kind)
    {
        var selected = kind.DefaultSize;
        var previewBox = new Border { Height = 210, Margin = new Thickness(0, 0, 0, 12) };
        PreviewHost? preview = null;

        void ShowPreview()
        {
            if (preview is not null)
            {
                _previews.Remove(preview);
                preview.Dispose();
            }
            preview = new PreviewHost(kind, selected);
            _previews.Add(preview);
            previewBox.Child = new Viewbox { Child = preview.Card, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 210, MaxWidth = 330 };
        }

        var sizes = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void PaintSizes()
        {
            foreach (DeskButton chip in sizes.Children) chip.Background = Equals(chip.Tag, selected) ? DeskUi.Blue : DeskUi.Inset;
        }
        foreach (var size in kind.Sizes)
        {
            var label = DeskUi.T(Desk.SizeName(size), 11.5, FontWeights.SemiBold);
            label.Margin = new Thickness(10, 0, 10, 0);
            var chosen = size;
            var chip = new DeskButton(label, () =>
            {
                selected = chosen;
                PaintSizes();
                ShowPreview();
            }, background: DeskUi.Inset, radius: 12) { Height = 26, Margin = new Thickness(0, 0, 6, 6), Tag = size };
            sizes.Children.Add(chip);
        }
        PaintSizes();
        ShowPreview();

        var name = DeskUi.T(kind.Name, 15, FontWeights.SemiBold);
        var description = DeskUi.T(kind.Description, 12, brush: DeskUi.Secondary);
        description.TextWrapping = TextWrapping.Wrap;
        description.Height = 34;
        description.Margin = new Thickness(0, 3, 0, 0);
        var addLabel = DeskUi.Row(DeskUi.Icon("IconPlus", 13, Brushes.White), DeskUi.Margin(DeskUi.T(Desk.L("Ekle", "Add"), 12.5, FontWeights.SemiBold, Brushes.White), 6));
        addLabel.Margin = new Thickness(14, 0, 16, 0);
        var add = new DeskButton(addLabel, () =>
        {
            _manager.Add(kind, selected);
            Activate();
        }, background: DeskUi.Blue, radius: 15) { Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };

        var stack = new StackPanel();
        stack.Children.Add(previewBox);
        stack.Children.Add(name);
        stack.Children.Add(description);
        stack.Children.Add(sizes);
        stack.Children.Add(add);
        return new Border
        {
            Width = 326,
            Margin = new Thickness(6),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(18),
            Background = Ui("#0DFFFFFF"),
            BorderBrush = Ui("#14FFFFFF"),
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }

    /// <summary>A widget rendered in the gallery: same view as on the desktop, never interactive.</summary>
    private sealed class PreviewHost : IDeskHost, IDisposable
    {
        private readonly Border _card;

        public PreviewHost(DeskKind kind, DeskSize size)
        {
            Item = new DeskItem { Kind = kind.Id, Size = size };
            Widget = kind.Create();
            Widget.Host = this;
            var dims = Desk.Dimensions(size);
            _card = new Border
            {
                Width = dims.Width,
                Height = dims.Height,
                CornerRadius = new CornerRadius(24),
                Background = DeskUi.CardFill,
                BorderBrush = DeskUi.CardStroke,
                BorderThickness = new Thickness(1),
                Padding = Desk.Padding(size),
                IsHitTestVisible = false
            };
            _card.SizeChanged += (_, e) => _card.Clip = new RectangleGeometry(new Rect(e.NewSize), 24, 24);
            _card.Child = Widget.Build();
            Widget.Attach();
            Widget.Tick();
        }

        public DeskItem Item { get; }
        public DeskWidget Widget { get; }
        public FrameworkElement Card => _card;
        public bool IsLive => true;
        public void Save() { }
        public void FocusInput(IInputElement element) { }

        public void Rebuild()
        {
            _card.Child = Widget.Build();
            Widget.Tick();
        }

        public void Dispose()
        {
            Widget.Detach();
            Widget.Dispose();
        }
    }
}
