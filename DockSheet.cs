using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace QiDock;

/// <summary>
/// A widget's card shown the way the bell's list is: the dock swells up out of the widget's tile into the card.
/// Used while the widgets sit in the dock; the card's own content is moved in and out by its widget popup.
/// </summary>
public sealed class DockSheet : EdgePanel
{
    // The popup card's padding (16, 14) less the panel's own space around its tiles.
    private static readonly Thickness BodyPadding = new(6, 4, 6, 4);
    private readonly Border _body;

    public event EventHandler? SheetOpened;
    public event EventHandler? SheetHidden;

    /// <param name="cardWidth">The popup card's width (padding and border included), as the widget asked for it.</param>
    public DockSheet(double cardWidth) : base(Math.Max(120, cardWidth - 22), "QiDock Widget")
    {
        _body = new Border { Padding = BodyPadding, Width = CardWidth };
        _body.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        _body.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5) Refit();
        };
    }

    public UIElement? Body
    {
        get => _body.Child;
        set => _body.Child = value;
    }

    protected override void Build()
    {
        Cards.Children.Clear();
        Cards.Children.Add(_body);
    }

    protected override void OnOpened() => SheetOpened?.Invoke(this, EventArgs.Empty);

    protected override void OnHidden() => SheetHidden?.Invoke(this, EventArgs.Empty);
}
