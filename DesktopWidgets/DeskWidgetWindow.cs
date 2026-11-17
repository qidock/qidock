using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace QiDock.DesktopWidgets;

/// <summary>
/// One desktop widget in its own small window. The window is owned by the desktop (Progman), so it
/// sits just above the wallpaper, under every app window, and survives "Show desktop". It never
/// takes focus unless a text box inside it is clicked. Drag anywhere that is not a control to move it.
/// </summary>
internal sealed class DeskWidgetWindow : Window, IDeskHost
{
    public const double Shadow = 16;
    private readonly DeskWidgetManager _manager;
    private readonly Border _card;
    private readonly Border _shadow;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private NativePoint? _press;
    private double _pressLeft, _pressTop;
    private bool _dragging;
    private bool _allowActivate;

    public DeskItem Item { get; }
    public DeskWidget Widget { get; }
    public DeskKind Kind { get; }
    public nint Handle { get; private set; }
    public bool IsLive { get; private set; }
    public bool ClosingByManager { get; set; }

    public DeskWidgetWindow(DeskWidgetManager manager, DeskItem item, DeskKind kind)
    {
        _manager = manager;
        Item = item;
        Kind = kind;
        Widget = kind.Create();
        Widget.Host = this;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Title = "QiDock Desktop Widget";
        SetResourceReference(FontFamilyProperty, "InterfaceFont");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // The shadow is a separate layer so content updates (a ticking clock) never re-render the blur.
        _shadow = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = DeskUi.CardFill,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 5, Direction = 270, Opacity = 0.38, RenderingBias = RenderingBias.Performance }
        };
        _card = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = DeskUi.CardFill,
            BorderBrush = DeskUi.CardStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14)
        };
        _card.SizeChanged += (_, e) => _card.Clip = new RectangleGeometry(new Rect(e.NewSize), 24, 24);
        var layers = new Grid { Margin = new Thickness(Shadow) };
        layers.Children.Add(_shadow);
        layers.Children.Add(_card);
        Content = layers;
        ApplySize();

        Left = item.X - Shadow;
        Top = item.Y - Shadow;

        _timer.Tick += (_, _) => Widget.Tick();
        PreviewMouseLeftButtonDown += OnPress;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnRelease;
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu();
        };
        Deactivated += (_, _) => _allowActivate = false;
        Rebuild();
    }

    private void ApplySize()
    {
        var size = Desk.Dimensions(Item.Size);
        _card.Width = _shadow.Width = size.Width;
        _card.Height = _shadow.Height = size.Height;
        // Strips are short; keep their content from touching the edges but use the height.
        _card.Padding = Desk.Padding(Item.Size);
    }

    public void Rebuild()
    {
        _card.Child = Widget.Build();
        if (IsLive) Widget.Tick();
    }

    public void Save() => _manager.Save();

    /// <summary>Starts (visible) or pauses (covered by a window) the widget's timers and data feeds.</summary>
    public void SetLive(bool live)
    {
        if (IsLive == live) return;
        IsLive = live;
        if (live)
        {
            Widget.Attach();
            Widget.Tick();
            if (Widget.Interval is { } interval)
            {
                _timer.Interval = interval;
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
            Widget.Detach();
        }
    }

    /// <summary>The widget's tick interval changed (a stopwatch started running).</summary>
    public void RestartTimer()
    {
        _timer.Stop();
        if (IsLive && Widget.Interval is { } interval)
        {
            _timer.Interval = interval;
            _timer.Start();
        }
    }

    public void ChangeSize(DeskSize size)
    {
        if (Item.Size == size) return;
        Item.Size = size;
        ApplySize();
        Rebuild();
        _manager.KeepOnScreen(this);
        Save();
    }

    public Rect Bounds => new(Item.X, Item.Y, Desk.Dimensions(Item.Size).Width, Desk.Dimensions(Item.Size).Height);

    public void FocusInput(IInputElement element)
    {
        _allowActivate = true;
        if (Handle != 0) SetForegroundWindow(Handle);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(element));
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        var title = new MenuItem { Header = Kind.Name, IsEnabled = false };
        if (Application.Current.TryFindResource("MenuSectionHeading") is Style heading) title.Style = heading;
        menu.Items.Add(title);
        if (Kind.Sizes.Length > 1)
        {
            var sizes = Entry("Boyut", "Size", "IconMaximize");
            foreach (var size in Kind.Sizes)
            {
                var entry = new MenuItem { Header = Desk.SizeName(size), IsChecked = size == Item.Size };
                var chosen = size;
                entry.Click += (_, _) => ChangeSize(chosen);
                sizes.Items.Add(entry);
            }
            menu.Items.Add(sizes);
        }
        var before = menu.Items.Count;
        Widget.AddMenuItems(menu);
        menu.Items.Add(new Separator());
        var add = Entry("Widget ekle…", "Add widget…", "IconPlus");
        add.Click += (_, _) => _manager.ShowGallery();
        menu.Items.Add(add);
        var remove = Entry("Masaüstünden kaldır", "Remove from desktop", "IconTrash");
        if (Application.Current.TryFindResource("DestructiveMenuItem") is Style destructive) remove.Style = destructive;
        remove.Click += (_, _) => _manager.Remove(this);
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private static MenuItem Entry(string tr, string en, string? icon)
    {
        var item = new MenuItem { Header = Desk.L(tr, en) };
        if (icon is not null && Application.Current.TryFindResource(icon) is Geometry geometry) item.Icon = geometry;
        return item;
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject) || !GetCursorPos(out var cursor)) return;
        _press = cursor;
        _pressLeft = Left;
        _pressTop = Top;
        _dragging = false;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_press is not { } press) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        if (!GetCursorPos(out var cursor)) return;
        var dx = cursor.X - press.X;
        var dy = cursor.Y - press.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;
            _dragging = true;
            CaptureMouse();
            Opacity = 0.88;
        }
        var scale = DpiScale();
        Left = _pressLeft + dx / scale;
        Top = _pressTop + dy / scale;
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) e.Handled = true;
        EndDrag();
    }

    private void EndDrag()
    {
        var moved = _dragging;
        _press = null;
        _dragging = false;
        if (!moved) return;
        ReleaseMouseCapture();
        Opacity = 1;
        var snapped = _manager.Snap(this, Left + Shadow, Top + Shadow);
        Item.X = snapped.X;
        Item.Y = snapped.Y;
        MoveTo(snapped);
        Save();
    }

    public void MoveTo(Point position)
    {
        Item.X = position.X;
        Item.Y = position.Y;
        Left = position.X - Shadow;
        Top = position.Y - Shadow;
    }

    private static bool IsInteractive(DependencyObject? node)
    {
        while (node is not null and not Window)
        {
            if (node is DeskButton or ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or Slider or Thumb or ScrollBar or ComboBox) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private double DpiScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        Handle = source.Handle;
        source.AddHook(WindowProc);
        var style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(Handle, GwlExStyle, new nint((style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow));
        AttachToDesktop();
    }

    /// <summary>Owned by the desktop window: above the wallpaper, below apps, kept by Win+D.</summary>
    public void AttachToDesktop()
    {
        var progman = FindWindow("Progman", null);
        if (Handle != 0 && progman != 0) SetWindowLongPtr(Handle, GwlpHwndParent, progman);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmMouseActivate = 0x0021;
        const int WmWindowPosChanging = 0x0046;
        const int MaNoActivate = 3;
        if (message == WmMouseActivate && !_allowActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }
        if (message == WmWindowPosChanging)
        {
            // Stay at the bottom of the z-order: clicks or activation must not lift the widget over apps.
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            if ((pos.Flags & SwpNoZOrder) == 0)
            {
                pos.InsertAfter = HwndBottom;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (IsLive) Widget.Detach();
        IsLive = false;
        Widget.Dispose();
        base.OnClosed(e);
        if (!ClosingByManager) _manager.WindowLost(this);
    }

    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExAppWindow = 0x00040000;
    private const uint SwpNoZOrder = 0x0004;
    private static readonly IntPtr HwndBottom = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
