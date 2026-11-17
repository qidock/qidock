using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QiDock;

/// <summary>
/// A StackPanel that never applies WPF's layout clip. Magnified dock tiles grow above the dock and
/// briefly push the row wider than its arranged size; a normal panel would then clip the whole row
/// (including the scaled tiles) to its bounds.
/// </summary>
public sealed class UnclippedStackPanel : StackPanel
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;
}
