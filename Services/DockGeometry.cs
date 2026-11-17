using System.Windows.Controls.Primitives;
using System.Windows.Media;
using QiDock.Models;

namespace QiDock.Services;

/// <summary>
/// The dock is laid out once, as if it stood on the bottom edge (its "frame"), and turned to the edge it really
/// stands on. Everything that is placed next to the dock (panels, previews, menus) asks here which way is "inward".
/// </summary>
public static class DockGeometry
{
    public static DockEdge Edge { get; set; } = DockEdge.Bottom;

    public static bool Vertical => Edge is DockEdge.Left or DockEdge.Right;

    /// <summary>Turns the bottom-edge frame to the real edge (linear part only): frame "down" points at the screen edge.</summary>
    public static Matrix FrameToScreen(DockEdge edge) => edge switch
    {
        DockEdge.Top => new Matrix(1, 0, 0, -1, 0, 0),   // mirrored top to bottom
        DockEdge.Left => new Matrix(0, 1, -1, 0, 0, 0),  // turned a quarter: down -> left, right -> down
        DockEdge.Right => new Matrix(0, 1, 1, 0, 0, 0),  // down -> right, right -> down
        _ => Matrix.Identity
    };

    /// <summary>Where things open next to a dock tile: away from the screen edge.</summary>
    public static PlacementMode Inward => Edge switch
    {
        DockEdge.Top => PlacementMode.Bottom,
        DockEdge.Left => PlacementMode.Right,
        DockEdge.Right => PlacementMode.Left,
        _ => PlacementMode.Top
    };
}
