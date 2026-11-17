using System.Windows;
using System.Windows.Threading;

namespace QiDock.DesktopWidgets;

/// <summary>
/// Keeps the desktop widgets: opens their windows, saves the layout, finds free spots, snaps them
/// to each other, and pauses the ones an app window fully covers.
/// </summary>
internal sealed class DeskWidgetManager : IDisposable
{
    private readonly DeskLayout _layout = DeskStore.Load();
    private readonly List<DeskWidgetWindow> _windows = [];
    private readonly DispatcherTimer _saveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _recreateTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
    private IReadOnlyList<Rect> _covers = [];
    private DeskGallery? _gallery;
    private bool _disposed;
    private bool _hidden;

    public DeskWidgetManager()
    {
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            DeskStore.Save(_layout);
        };
        _recreateTimer.Tick += (_, _) =>
        {
            _recreateTimer.Stop();
            Reopen();
        };
    }

    public IReadOnlyList<DeskItem> Items => _layout.Items;

    public void Start()
    {
        if (!_layout.Initialized)
        {
            AddDefaults();
            _layout.Initialized = true;
            Save();
        }
        foreach (var item in _layout.Items.ToList()) Open(item);
    }

    private DeskWidgetWindow? Open(DeskItem item)
    {
        if (DeskCatalog.Find(item.Kind) is not { } kind) return null;
        if (!kind.Sizes.Contains(item.Size)) item.Size = kind.DefaultSize;
        var window = new DeskWidgetWindow(this, item, kind);
        _windows.Add(window);
        window.Show();
        KeepOnScreen(window);
        window.SetLive(!_hidden && IsUncovered(window));
        if (_hidden) window.Hide();
        return window;
    }

    public void Add(DeskKind kind, DeskSize size)
    {
        var item = new DeskItem { Kind = kind.Id, Size = size };
        var spot = FindSpot(Desk.Dimensions(size));
        item.X = spot.X;
        item.Y = spot.Y;
        _layout.Items.Add(item);
        Open(item);
        Save();
    }

    public void Remove(DeskWidgetWindow window)
    {
        _layout.Items.Remove(window.Item);
        _windows.Remove(window);
        window.ClosingByManager = true;
        window.Close();
        Save();
    }

    public void Save()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>The system closed a widget window (Explorer restarted and took its owner along).</summary>
    public void WindowLost(DeskWidgetWindow window)
    {
        _windows.Remove(window);
        if (_disposed) return;
        _recreateTimer.Stop();
        _recreateTimer.Start();
    }

    private void Reopen()
    {
        if (_disposed) return;
        foreach (var item in _layout.Items.Where(i => _windows.All(w => !ReferenceEquals(w.Item, i))).ToList()) Open(item);
    }

    /// <summary>Explorer came back (TaskbarCreated): hook the windows to the new desktop.</summary>
    public void Reattach()
    {
        foreach (var window in _windows) window.AttachToDesktop();
        Reopen();
    }

    public void SettingsChanged()
    {
        WeatherHub.Instance.CityMayHaveChanged();
        CalendarHub.Instance.UrlMayHaveChanged();
        foreach (var window in _windows) window.Widget.SettingsChanged();
        _gallery?.SettingsChanged();
    }

    /// <summary>App window rectangles (physical pixels) on screen; widgets under one of them pause.</summary>
    public void UpdateCovers(IReadOnlyList<Rect> covers)
    {
        _covers = covers;
        foreach (var window in _windows) window.SetLive(!_hidden && IsUncovered(window));
    }

    /// <summary>
    /// The desktop with files shows only its folders and files: the widgets step away (and pause) meanwhile and
    /// come back on the clean desktop.
    /// </summary>
    public void SetHidden(bool hidden)
    {
        if (_hidden == hidden) return;
        _hidden = hidden;
        foreach (var window in _windows)
        {
            if (hidden)
            {
                window.SetLive(false);
                window.Hide();
            }
            else
            {
                window.Show();
                window.SetLive(IsUncovered(window));
            }
        }
    }

    private bool IsUncovered(DeskWidgetWindow window)
    {
        if (_covers.Count == 0) return true;
        var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var b = window.Bounds;
        var pixels = new Rect(b.X * scale, b.Y * scale, b.Width * scale, b.Height * scale);
        return !_covers.Any(cover => cover.Contains(pixels));
    }

    public void ShowGallery()
    {
        if (_gallery is { } open)
        {
            open.Activate();
            return;
        }
        _gallery = new DeskGallery(this);
        _gallery.Closed += (_, _) => _gallery = null;
        _gallery.Show();
        _gallery.Activate();
    }

    /// <summary>Lines a dragged widget up with its neighbours (edges or one gap apart) and keeps it on screen.</summary>
    public Point Snap(DeskWidgetWindow window, double x, double y)
    {
        const double Magnet = 14;
        var size = Desk.Dimensions(window.Item.Size);
        double? bestX = null, bestY = null;
        double distX = Magnet, distY = Magnet;
        foreach (var other in _windows.Where(w => !ReferenceEquals(w, window)).Select(w => w.Bounds))
        {
            foreach (var candidate in new[] { other.Left, other.Right + Desk.Gap, other.Left - Desk.Gap - size.Width, other.Right - size.Width })
            {
                var d = Math.Abs(x - candidate);
                if (d < distX) { distX = d; bestX = candidate; }
            }
            foreach (var candidate in new[] { other.Top, other.Bottom + Desk.Gap, other.Top - Desk.Gap - size.Height, other.Bottom - size.Height })
            {
                var d = Math.Abs(y - candidate);
                if (d < distY) { distY = d; bestY = candidate; }
            }
        }
        var point = new Point(bestX ?? Math.Round(x / 4) * 4, bestY ?? Math.Round(y / 4) * 4);
        return Clamp(point, size);
    }

    public void KeepOnScreen(DeskWidgetWindow window)
    {
        var clamped = Clamp(new Point(window.Item.X, window.Item.Y), Desk.Dimensions(window.Item.Size));
        if (clamped.X != window.Item.X || clamped.Y != window.Item.Y)
        {
            window.MoveTo(clamped);
            Save();
        }
    }

    private static Point Clamp(Point point, Size size)
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        return new Point(
            Math.Clamp(point.X, left, Math.Max(left, right - size.Width)),
            Math.Clamp(point.Y, top, Math.Max(top, bottom - size.Height)));
    }

    /// <summary>First free place, filling columns from the left below the widget bar.</summary>
    private Point FindSpot(Size size)
    {
        var area = SystemParameters.WorkArea;
        var startX = area.Left + 32;
        var startY = area.Top + 96;
        var taken = _layout.Items.Select(i => new Rect(i.X - Desk.Gap / 2, i.Y - Desk.Gap / 2,
            Desk.Dimensions(i.Size).Width + Desk.Gap, Desk.Dimensions(i.Size).Height + Desk.Gap)).ToList();
        const double Step = (Desk.StripHeight + Desk.Gap) / 2;
        for (var x = startX; x + size.Width <= area.Right - 32; x += Desk.Unit + Desk.Gap)
        {
            for (var y = startY; y + size.Height <= area.Bottom - 40; y += Step)
            {
                var rect = new Rect(x, y, size.Width, size.Height);
                if (!taken.Any(t => t.IntersectsWith(rect))) return new Point(x, y);
            }
        }
        return new Point(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2);
    }

    /// <summary>A first layout so the feature shows itself; everything can be moved or removed.</summary>
    private void AddDefaults()
    {
        var area = SystemParameters.WorkArea;
        var x = area.Left + 32;
        var y = area.Top + 96;
        var step = Desk.Unit + Desk.Gap;
        void Put(string kind, DeskSize size, double left, double top) =>
            _layout.Items.Add(new DeskItem { Kind = kind, Size = size, X = left, Y = top });
        Put("analog", DeskSize.Small, x, y);
        Put("weather", DeskSize.Small, x + step, y);
        Put("calendar", DeskSize.Medium, x, y + step);
        Put("music", DeskSize.Strip, x, y + step * 2);
        Put("cpu", DeskSize.Mini, x, y + step * 2 + Desk.StripHeight + Desk.Gap);
        Put("ram", DeskSize.Mini, x + step, y + step * 2 + Desk.StripHeight + Desk.Gap);
    }

    public void Dispose()
    {
        _disposed = true;
        _saveTimer.Stop();
        _recreateTimer.Stop();
        DeskStore.Save(_layout);
        _gallery?.Close();
        foreach (var window in _windows.ToList())
        {
            window.ClosingByManager = true;
            window.Close();
        }
        _windows.Clear();
    }
}
