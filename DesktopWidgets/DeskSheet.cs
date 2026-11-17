#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiDock.Models;

namespace QiDock.DesktopWidgets;

/// <summary>Debug builds only: renders every desktop widget in every size to one PNG for review.</summary>
internal static class DeskSheet
{
    public static void Render(string path, Func<WidgetSettings> settings, Action done)
    {
        Desk.Settings = settings;
        var panel = new WrapPanel { Width = 2300, Background = QiDock.Widgets.Ui.Vertical("#FF5B86C5", "#FF2A4C8C") };
        panel.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "InterfaceFont");
        TextOptions.SetTextFormattingMode(panel, TextFormattingMode.Ideal);
        var hosts = new List<SheetHost>();
        foreach (var kind in DeskCatalog.All)
        foreach (var size in kind.Sizes)
        {
            var host = new SheetHost(kind, size);
            hosts.Add(host);
            var label = DeskUi.T($"{kind.Id} / {size}", 13, FontWeights.SemiBold, Brushes.White);
            var cell = new StackPanel { Margin = new Thickness(14) };
            cell.Children.Add(label);
            cell.Children.Add(DeskUi.Margin(host.Card, 0, 6));
            panel.Children.Add(cell);
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var host in hosts) host.Widget.Tick();
                panel.Measure(new Size(panel.Width, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                panel.UpdateLayout();
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            foreach (var host in hosts) host.Widget.Detach();
            done();
        };
        panel.Measure(new Size(panel.Width, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        timer.Start();
    }

    private sealed class SheetHost : IDeskHost
    {
        public SheetHost(DeskKind kind, DeskSize size)
        {
            Item = new DeskItem { Kind = kind.Id, Size = size };
            Widget = kind.Create();
            Widget.Host = this;
            var dims = Desk.Dimensions(size);
            Card = new Border
            {
                Width = dims.Width,
                Height = dims.Height,
                CornerRadius = new CornerRadius(24),
                Background = DeskUi.CardFill,
                BorderBrush = DeskUi.CardStroke,
                BorderThickness = new Thickness(1),
                Padding = Desk.Padding(size),
                Child = Widget.Build()
            };
            Card.Clip = new RectangleGeometry(new Rect(0, 0, dims.Width, dims.Height), 24, 24);
            Widget.Attach();
        }

        public DeskItem Item { get; }
        public DeskWidget Widget { get; }
        public Border Card { get; }
        public bool IsLive => true;
        public void Save() { }
        public void FocusInput(IInputElement element) { }
        public void Rebuild() => Card.Child = Widget.Build();
    }
}
#endif
