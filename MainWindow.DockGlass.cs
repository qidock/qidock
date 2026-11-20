using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Services;
using QiDock.Services.Widgets;

namespace QiDock;

/// <summary>
/// The dock as frosted glass. The dock keeps its own strip of the screen, so behind it is the wallpaper: the dock
/// shows that part of the wallpaper, softened (the same picture the desktop cards use), under its own colour made
/// thinner. Nothing is blurred live; the picture is only placed again when the dock changes size or place. With
/// auto-hide a window can be behind the dock: it then shows the wallpaper's colours, as a tint, not that window.
/// </summary>
public partial class MainWindow
{
    private bool _dockGlassHooked;
    private bool _dockGlassOn;
    private DispatcherTimer? _dockGlassWallpaper;

    private void ApplyDockGlass()
    {
        if (!_dockGlassHooked)
        {
            _dockGlassHooked = true;
            DockSurface.SizeChanged += (_, _) => RefreshDockGlass();
            DockSurface.IsVisibleChanged += (_, _) => RefreshDockGlass();
            LocationChanged += (_, _) => RefreshDockGlass();
            ThemeManager.MaterialsChanged += (_, _) => RefreshDockGlass();
            // Windows writes the new wallpaper a moment after it says the desktop changed.
            _dockGlassWallpaper = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.6) };
            _dockGlassWallpaper.Tick += (_, _) =>
            {
                _dockGlassWallpaper.Stop();
                WallpaperGlass.Refresh();
                RefreshDockGlass();
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != Microsoft.Win32.UserPreferenceCategory.Desktop) return;
                Dispatcher.BeginInvoke(() =>
                {
                    _dockGlassWallpaper.Stop();
                    _dockGlassWallpaper.Start();
                });
            };
            WallpaperGallery.Applied += () =>
            {
                _dockGlassWallpaper.Stop();
                _dockGlassWallpaper.Start();
            };
            Closed += (_, _) => _dockGlassWallpaper.Stop();
        }
        _dockGlassOn = _preferences.Current.DockGlass;
        RefreshDockGlass();
    }

    /// <summary>
    /// Glass when it is on and the theme is "Cam" (the other themes bring their own colours to the dock: under glass
    /// only their writing changed), the theme's material otherwise. The dock places the
    /// one picture of the screen behind itself, so its growing, shrinking and sliding never draw the glass again.
    /// </summary>
    private void RefreshDockGlass()
    {
        var standing = EdgePanel.StandsOnDock && !_panelOnTopBar;
        var before = (GlassLook.DockVeil, GlassLook.WindowBehind);
        GlassLook.WindowBehind = _autoHideController?.WindowBehind == true;
        var glass = _dockGlassOn && ThemeManager.CurrentStyle == ThemeStyles.Glass;
        if (glass && WallpaperGlass.Image is null) WallpaperGlass.Refresh();
        if (!glass || WallpaperGlass.Image is null || DockToScreen() is not { } toScreen)
        {
            GlassLook.DockVeil = null;
            GlassLook.Tiles(Resources, false);
            if (before.DockVeil is not null) GlassLook.RaiseDockChanged();
            if (standing) return; // the solid piece the card set up stays
            DockSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DockMaterial");
            DockSurface.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DockBorderMaterial");
            return;
        }
        var area = toScreen.Bounds;
        GlassLook.DockVeil = GlassLook.Veil(WallpaperGlass.Light(area));
        GlassLook.Tiles(Resources, true);
        DockSurface.Background = GlassLook.Dock(toScreen.Matrix);
        DockSurface.BorderBrush = GlassLook.Rim;
        if (before != (GlassLook.DockVeil, GlassLook.WindowBehind)) GlassLook.RaiseDockChanged();
    }

    /// <summary>
    /// How the dock's surface maps onto the screen (screen units) where it rests, also while it is slid away and on
    /// any edge (the dock is turned for the side and top edges), and the screen area it covers.
    /// </summary>
    private (Matrix Matrix, Rect Bounds)? DockToScreen()
    {
        if (DockSurface.ActualWidth <= 0 || DockSurface.ActualHeight <= 0 || !DockSurface.IsVisible) return null;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return null;
        if (DockSurface.TransformToAncestor(this) is not Transform toWindow) return null;
        // Without the slide of hiding: where the dock rests.
        var matrix = new Matrix(1, 0, 0, 1, -DockSurfaceTranslate.X, -DockSurfaceTranslate.Y);
        matrix.Append(toWindow.Value);
        var origin = target.TransformFromDevice.Transform(PointToScreen(new Point(0, 0)));
        matrix.Translate(origin.X, origin.Y);
        var bounds = new Rect(0, 0, DockSurface.ActualWidth, DockSurface.ActualHeight);
        bounds.Transform(matrix);
        return (matrix, bounds);
    }
}
