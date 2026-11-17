using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QiDock.Models;
using QiDock.Services;

namespace QiDock;

/// <summary>
/// The dock can stand on any screen edge. It is laid out as if it stood on the bottom edge and the whole layout
/// is turned to the real edge (<see cref="DockGeometry.FrameToScreen"/>), so magnification, hiding, reordering and
/// the running dots all keep working unchanged. Pictures and text are turned back so they always stand upright.
/// </summary>
public partial class MainWindow
{
    private DockEdge? _appliedEdge;

    private DockEdge? _edgeOverride;

    private DockEdge Edge => _edgeOverride ?? _preferences.Current.Edge;

    private void ApplyDockEdge()
    {
        var edge = Edge;
        DockGeometry.Edge = edge;
        if (_appliedEdge == edge) return;
        var first = _appliedEdge is null;
        _appliedEdge = edge;
        ResetMagnification();
        _thumbnailPreview.Hide();
        DockHost.LayoutTransform = edge == DockEdge.Bottom ? Transform.Identity : new MatrixTransform(DockGeometry.FrameToScreen(edge));
        StatusText.LayoutTransform = UprightTransform();
        OrientAllIcons();
        _placedWidgets = null;
        ApplyWidgetPlacement();
        if (first) return; // the taskbar session starts with the edge already set
        if (_replacementSession?.IsActive == true) _replacementSession.SetEdge(edge);
        else PositionDock();
    }

    /// <summary>Undoes the dock's turn for something that must read upright (text, pictures).</summary>
    private static Transform UprightTransform()
    {
        if (DockGeometry.Edge == DockEdge.Bottom) return Transform.Identity;
        var counter = DockGeometry.FrameToScreen(DockGeometry.Edge);
        counter.Invert();
        return new MatrixTransform(counter);
    }

    /// <summary>
    /// Turns a tile's picture upright about its own centre, before its magnification and bounce. Those keep working
    /// in the dock's frame: a tile still grows out of the screen edge and bounces away from it.
    /// </summary>
    private static void OrientIcon(FrameworkElement icon)
    {
        var (_, _) = GetIconTransforms(icon);
        if (icon.RenderTransform is not TransformGroup group) return;
        var orient = group.Children.OfType<MatrixTransform>().FirstOrDefault();
        if (orient is null)
        {
            if (DockGeometry.Edge == DockEdge.Bottom) return;
            orient = new MatrixTransform();
            group.Children.Insert(0, orient);
        }
        var counter = DockGeometry.FrameToScreen(DockGeometry.Edge);
        counter.Invert();
        var tile = Application.Current.Resources["DockTileSize"] is double size ? size : 48;
        var width = double.IsNaN(icon.Width) ? tile : icon.Width;
        var height = double.IsNaN(icon.Height) ? tile : icon.Height;
        var origin = icon.RenderTransformOrigin;
        // The centre, seen from the transform origin (the tile's foot).
        var cx = width * (0.5 - origin.X);
        var cy = height * (0.5 - origin.Y);
        var matrix = Matrix.Identity;
        matrix.Translate(-cx, -cy);
        matrix.Append(counter);
        matrix.Translate(cx, cy);
        orient.Matrix = matrix;
    }

    private void OrientAllIcons()
    {
        foreach (var button in GetAllDockButtons())
        {
            if (GetButtonIcon(button) is { } icon) OrientIcon(icon);
            PointTooltip(button);
        }
    }

    /// <summary>Tooltips open inward, clear of the magnified tile.</summary>
    private static void PointTooltip(Button button)
    {
        var placement = DockGeometry.Inward;
        ToolTipService.SetPlacement(button, placement);
        ToolTipService.SetVerticalOffset(button, placement switch { System.Windows.Controls.Primitives.PlacementMode.Top => -44, System.Windows.Controls.Primitives.PlacementMode.Bottom => 44, _ => 0 });
        ToolTipService.SetHorizontalOffset(button, placement switch { System.Windows.Controls.Primitives.PlacementMode.Right => 44, System.Windows.Controls.Primitives.PlacementMode.Left => -44, _ => 0 });
    }

    /// <summary>Where a panel grows out of this dock element (screen DIPs), or null while the dock is not laid out.</summary>
    private EdgePanel.DockAnchor? DockAnchorOf(FrameworkElement element)
    {
        _panelOnTopBar = false;
        var tile = element is Button { Content: FrameworkElement face } ? face : element;
        if (!IsVisible || tile.ActualWidth <= 0 || DockSurface.ActualHeight <= 0 || !tile.IsDescendantOf(this)) return null;
        // Bounds before magnification and bounce: the tile's own box, through the dock's turn.
        var bounds = tile.TransformToAncestor(this).TransformBounds(new Rect(tile.RenderSize));
        // Where the dock rests, not where its hiding slide has it now (a panel brings the dock back up as it opens).
        if (VisualTreeHelper.GetParent(DockSurface) is not Visual dockParent) return null;
        var surface = dockParent.TransformToAncestor(this).TransformBounds(new Rect((Point)VisualTreeHelper.GetOffset(DockSurface), DockSurface.RenderSize));
        var round = DockSurface.CornerRadius.TopLeft + 6; // clear of the dock's rounded ends
        var (dockStart, dockEnd) = DockGeometry.Vertical
            ? (Top + surface.Top + round, Top + surface.Bottom - round)
            : (Left + surface.Left + round, Left + surface.Right - round);
        return Edge switch
        {
            DockEdge.Top => new EdgePanel.DockAnchor(DockEdge.Top, Left + (bounds.Left + bounds.Right) / 2, bounds.Width, Top + surface.Bottom, dockStart, dockEnd),
            DockEdge.Left => new EdgePanel.DockAnchor(DockEdge.Left, Top + (bounds.Top + bounds.Bottom) / 2, bounds.Height, Left + surface.Right, dockStart, dockEnd),
            DockEdge.Right => new EdgePanel.DockAnchor(DockEdge.Right, Top + (bounds.Top + bounds.Bottom) / 2, bounds.Height, Left + surface.Left, dockStart, dockEnd),
            _ => new EdgePanel.DockAnchor(DockEdge.Bottom, Left + (bounds.Left + bounds.Right) / 2, bounds.Width, Top + surface.Top, dockStart, dockEnd)
        };
    }

    /// <summary>
    /// Leaves the dock's inner edge out under a panel standing on it, between two screen points on the line where
    /// the panel's foot ends (the cut goes down to exactly that line).
    /// </summary>
    private void CutDockSeam((Point From, Point To)? seam)
    {
        if (seam is not { } line || DockSurface.ActualWidth <= 0)
        {
            DockSurface.OpacityMask = null;
            return;
        }
        var a = TranslatePoint(new Point(line.From.X - Left, line.From.Y - Top), DockSurface);
        var b = TranslatePoint(new Point(line.To.X - Left, line.To.Y - Top), DockSurface);
        var depth = Math.Max(0, Math.Max(a.Y, b.Y));
        DockSurface.OpacityMask = EdgePanel.NotchMask(new Size(DockSurface.ActualWidth, DockSurface.ActualHeight), Math.Min(a.X, b.X), Math.Max(a.X, b.X), depth);
    }
}
