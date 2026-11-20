using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using QiDock.Services.Widgets;

namespace QiDock.DesktopWidgets;

/// <summary>
/// The surface of one desktop card. As glass (the default): the wallpaper behind it, softened, under a veil that is
/// darker where the picture is bright so white text stays readable, a soft sheen from above, and a thin rim that
/// catches the light on the top-left and bottom-right edges. Without glass, or with no wallpaper to read: the dark
/// card. The layers are plain fills; the shadow is its own layer so a ticking widget never redraws its blur.
/// </summary>
internal sealed class DeskCardFace
{
    public const double Radius = 24;

    /// <summary>Glass or the dark card, for every widget on the desktop (kept in the widget layout).</summary>
    public static bool GlassEnabled { get; set; } = true;

    private static readonly Brush Rim = GlassLook.Rim;
    private static readonly Brush Sheen = GlassLook.Sheen;
    private static readonly Brush Bare = QiDock.Widgets.Ui.Solid("#01000000");

    private readonly Border _shadow;
    private readonly Border _glass;
    private readonly Border _veil;
    private readonly ImageBrush _picture = new() { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
    private readonly DropShadowEffect _shadowEffect;
    private bool _bare;

    /// <summary>The widget covers the card with its own picture: no glass, veil or light rim (they showed at its edge).</summary>
    public bool Covered { get; set; }
    private Rect _area;

    public DeskCardFace()
    {
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        _shadowEffect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 5, Direction = 270, Opacity = 0.38, RenderingBias = RenderingBias.Performance };
        _shadow = new Border { CornerRadius = new CornerRadius(Radius), Background = DeskUi.CardFill, Effect = _shadowEffect };
        _glass = new Border { CornerRadius = new CornerRadius(Radius), Background = _picture };
        _veil = new Border { CornerRadius = new CornerRadius(Radius), Child = new Border { CornerRadius = new CornerRadius(Radius), Background = Sheen } };
        Card = new Border { CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1), Padding = new Thickness(14) };
        Card.SizeChanged += (_, e) => Card.Clip = new RectangleGeometry(new Rect(e.NewSize), Radius, Radius);
        Root = new Grid();
        Root.Children.Add(_shadow);
        Root.Children.Add(_glass);
        Root.Children.Add(_veil);
        Root.Children.Add(Card);
        Apply();
    }

    public Grid Root { get; }

    /// <summary>The card itself: its child is the widget.</summary>
    public Border Card { get; }

    public void SetSize(Size size, Thickness padding, bool bare)
    {
        foreach (var layer in new[] { _shadow, _glass, _veil, Card })
        {
            layer.Width = size.Width;
            layer.Height = size.Height;
        }
        Card.Padding = bare ? new Thickness(0) : padding;
        _bare = bare;
        Apply();
    }

    /// <summary>Where the card is on the screen (screen units), so the glass shows the wallpaper behind it.</summary>
    public void Place(Rect area)
    {
        if (_area == area) return;
        _area = area;
        if (_glass.Visibility == Visibility.Visible) Look();
    }

    /// <summary>The wallpaper or the glass setting changed.</summary>
    public void Apply()
    {
        if (_bare)
        {
            // Straight on the wallpaper: no card, no shadow. An almost invisible fill keeps the whole area draggable
            // (fully clear pixels of a layered window let clicks through).
            _shadow.Visibility = _glass.Visibility = _veil.Visibility = Visibility.Collapsed;
            Card.Background = Bare;
            Card.BorderBrush = Brushes.Transparent;
            return;
        }
        _shadow.Visibility = Visibility.Visible;
        var glass = GlassEnabled && WallpaperGlass.Image is not null;
        if (!glass)
        {
            _glass.Visibility = _veil.Visibility = Visibility.Collapsed;
            Card.Background = DeskUi.CardFill;
            Card.BorderBrush = DeskUi.CardStroke;
            _shadowEffect.Opacity = 0.38;
            _shadowEffect.BlurRadius = 24;
            return;
        }
        if (Covered)
        {
            _glass.Visibility = _veil.Visibility = Visibility.Collapsed;
            Card.Background = Brushes.Transparent;
            Card.BorderBrush = Brushes.Transparent;
            _shadowEffect.Opacity = 0.3;
            _shadowEffect.BlurRadius = 26;
            return;
        }
        _glass.Visibility = _veil.Visibility = Visibility.Visible;
        Card.Background = Brushes.Transparent;
        Card.BorderBrush = Rim;
        // Glass casts a lighter, wider shadow than a solid card.
        _shadowEffect.Opacity = 0.24;
        _shadowEffect.BlurRadius = 30;
        Look();
    }

    private void Look()
    {
        if (WallpaperGlass.Image is null || _area.Width <= 0 || double.IsNaN(_area.X) || double.IsNaN(_area.Y)) return;
        // Darker over a bright picture, lighter over a dark one: white text keeps about the same contrast.
        var veil = GlassLook.Veil(WallpaperGlass.Light(_area));
        var size = new Size(_glass.Width, _glass.Height);
        if (double.IsNaN(size.Width) || size.Width <= 0) size = _area.Size;
        if (GlassLook.Surface(new Rect(size), _area, veil, sheen: false, frost: false) is { } glass)
        {
            _glass.Background = glass;
            _veil.Background = null;
            return;
        }
        // Could not draw the glass picture: the veil over the picture brush, as before.
        _picture.ImageSource = WallpaperGlass.Image;
        _picture.Viewbox = WallpaperGlass.Behind(_area);
        _glass.Background = _picture;
        _veil.Background = new SolidColorBrush(veil);
    }

}
