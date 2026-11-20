using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using QiDock.Services.Widgets;

namespace QiDock.DesktopWidgets.Kinds;

/// <summary>
/// A large clock straight on the wallpaper, in one of several styles (as on a phone's lock screen), coloured from
/// the wallpaper behind it, in a chosen colour or in a two-colour blend. No card behind it; it scales to the chosen
/// size. Redrawn once a minute.
/// </summary>
internal sealed class BigClockWidget : DeskWidget
{
    private const double DesignWidth = 700;

    /// <summary>The styles, in menu order: id, Turkish and English name.</summary>
    public static readonly (string Id, string Tr, string En)[] Styles =
    [
        ("glass", "Cam", "Glass"),
        ("lock", "Kilit ekranı", "Lock screen"),
        ("thin", "İnce", "Thin"),
        ("heavy", "Kalın", "Heavy"),
        ("stacked", "Üst üste", "Stacked"),
        ("split", "Yan yana", "Side by side"),
        ("twotone", "İki renk", "Two-tone"),
        ("serif", "Zarif", "Elegant"),
        ("outline", "Dış hat", "Outline"),
        ("neon", "Neon", "Neon"),
        ("mono", "Daktilo", "Typewriter"),
        ("words", "Yazıyla", "In words")
    ];

    /// <summary>Colours: id (stored), Turkish and English name, and one colour or two for a blend.</summary>
    public static readonly (string Id, string Tr, string En, Color From, Color? To)[] Palette =
    [
        ("white", "Beyaz", "White", Colors.White, null),
        ("black", "Siyah", "Black", Rgb(0x16, 0x16, 0x18), null),
        ("cream", "Krem", "Cream", Rgb(0xF6, 0xEB, 0xD9), null),
        ("pink", "Pembe", "Pink", Rgb(0xFF, 0xA6, 0xC9), null),
        ("orange", "Turuncu", "Orange", Rgb(0xFF, 0xB3, 0x6B), null),
        ("yellow", "Sarı", "Yellow", Rgb(0xFF, 0xE0, 0x71), null),
        ("green", "Yeşil", "Green", Rgb(0x9B, 0xE8, 0x9B), null),
        ("teal", "Turkuaz", "Teal", Rgb(0x7F, 0xE3, 0xD8), null),
        ("blue", "Mavi", "Blue", Rgb(0x8E, 0xC5, 0xFF), null),
        ("purple", "Mor", "Purple", Rgb(0xC6, 0xA8, 0xFF), null),
        ("red", "Kırmızı", "Red", Rgb(0xFF, 0x7A, 0x7A), null),
        ("sunset", "Gün batımı", "Sunset", Rgb(0xFF, 0xC0, 0x6B), Rgb(0xFF, 0x6F, 0xA8)),
        ("ocean", "Okyanus", "Ocean", Rgb(0x7F, 0xE8, 0xF0), Rgb(0x5C, 0x8D, 0xFF)),
        ("aurora", "Kuzey ışığı", "Aurora", Rgb(0x8C, 0xF5, 0xC0), Rgb(0xB0, 0x8C, 0xFF))
    ];

    private static readonly FontFamily Thin = new("Bahnschrift Light, Segoe UI Variable Display Light, Segoe UI Light");
    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily Black = new("Segoe UI Black, Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily Condensed = new("Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI");
    private static readonly FontFamily Serif = new("Sitka Display, Georgia, Cambria");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    private TextBlock? _date, _time, _minutes, _words, _side;
    private UniformGrid? _day;
    private Path? _outline;
    private string _shown = "";
    private Color _tint = Colors.White;
    private Color? _blend;

    public override bool Bare => true;

    public override TimeSpan? Interval => TimeSpan.FromSeconds(1);

    private string Style => Styles.Any(s => s.Id == Option("style")) ? Option("style")! : "glass";

    /// <summary>Null: from the wallpaper. Older layouts stored "white" the same way.</summary>
    private string? ColorId => Palette.Any(p => p.Id == Option("tint")) ? Option("tint") : null;

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>Languages the date can be written in, each named in its own language.</summary>
    public static readonly (string Code, string Name)[] Languages =
    [
        ("tr", "Türkçe"), ("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"),
        ("it", "Italiano"), ("pt", "Português"), ("nl", "Nederlands"), ("ru", "Русский"), ("ar", "العربية"),
        ("ja", "日本語"), ("zh", "中文"), ("ko", "한국어")
    ];

    /// <summary>Null: the app's language.</summary>
    private string? LanguageId => Languages.Any(l => l.Code == Option("lang")) ? Option("lang") : null;

    private CultureInfo DateCulture()
    {
        if (LanguageId is not { } code) return Desk.Culture;
        try
        {
            return CultureInfo.GetCultureInfo(code == "zh" ? "zh-CN" : code);
        }
        catch (CultureNotFoundException)
        {
            return Desk.Culture;
        }
    }

    public override FrameworkElement Build()
    {
        _shown = "";
        _date = _time = _minutes = _words = _side = null;
        _day = null;
        _outline = null;
        var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.2, RenderingBias = RenderingBias.Performance };
        var stack = new StackPanel { Width = DesignWidth, Effect = Style == "neon" ? null : shadow };
        switch (Style)
        {
            case "lock":
                stack.Children.Add(_date = Line(Display, 46, FontWeights.SemiBold, new Thickness(0, 0, 0, -18)));
                stack.Children.Add(_time = Digits(Display, 270, FontWeights.Bold));
                break;
            case "thin":
                stack.Children.Add(_date = Line(Display, 44, FontWeights.Normal, new Thickness(0, 0, 0, -22)));
                stack.Children.Add(_time = Digits(Display, 290, FontWeights.Light));
                break;
            case "heavy":
                stack.Children.Add(_date = Line(Display, 42, FontWeights.Bold, new Thickness(0, 0, 0, -6)));
                stack.Children.Add(_time = Digits(Black, 250, FontWeights.Black, 1.0));
                break;
            case "stacked":
                // Hours over minutes in tall, narrow numbers.
                stack.Children.Add(_date = Line(Display, 40, FontWeights.SemiBold, new Thickness(0, 0, 0, -6)));
                stack.Children.Add(_time = Digits(Condensed, 250, FontWeights.Normal, 1.0));
                stack.Children.Add(_minutes = Digits(Condensed, 250, FontWeights.Normal, 1.0));
                _minutes.Margin = new Thickness(0, -34, 0, 0);
                break;
            case "split":
            {
                // The time on the left, the weekday and date beside it behind a thin line.
                var row = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(_time = Digits(Display, 190, FontWeights.SemiBold, 1.0));
                var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0) };
                side.Children.Add(_side = Line(Display, 40, FontWeights.Bold, new Thickness(0)));
                side.Children.Add(_date = Line(Display, 34, FontWeights.Normal, new Thickness(0, 2, 0, 0)));
                _side.HorizontalAlignment = _date.HorizontalAlignment = HorizontalAlignment.Left;
                var bar = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(22, 6, 0, 6), Child = side, VerticalAlignment = VerticalAlignment.Center };
                side.Margin = new Thickness(0);
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                stack.Children.Add(row);
                break;
            }
            case "twotone":
                stack.Children.Add(_date = Line(Display, 44, FontWeights.SemiBold, new Thickness(0, 0, 0, -20)));
                stack.Children.Add(_time = Digits(Display, 270, FontWeights.Bold));
                break;
            case "serif":
                stack.Children.Add(_date = Line(Serif, 44, FontWeights.Normal, new Thickness(0, 0, 0, -56)));
                stack.Children.Add(_time = Digits(Serif, 270, FontWeights.Normal));
                break;
            case "outline":
                stack.Children.Add(_date = Line(Display, 44, FontWeights.SemiBold, new Thickness(0, 0, 0, 6)));
                stack.Children.Add(_outline = new Path { HorizontalAlignment = HorizontalAlignment.Center, StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round });
                break;
            case "neon":
                stack.Children.Add(_date = Line(Thin, 46, FontWeights.Normal, new Thickness(0, 0, 0, -2)));
                stack.Children.Add(_time = Digits(Thin, 280, FontWeights.Normal));
                break;
            case "mono":
                stack.Children.Add(_time = Digits(Mono, 200, FontWeights.SemiBold, 1.0));
                stack.Children.Add(_date = Line(Mono, 36, FontWeights.Normal, new Thickness(0, 4, 0, 0)));
                break;
            case "words":
                stack.Children.Add(_words = new TextBlock
                {
                    FontFamily = Display,
                    FontSize = 104,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Left,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    LineHeight = 108,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                stack.Children.Add(_date = Line(Display, 38, FontWeights.SemiBold, new Thickness(0, 22, 0, 0)));
                break;
            default:
                stack.Children.Add(_date = Line(Thin, 50, FontWeights.Normal, new Thickness(150, 0, 0, -26)));
                stack.Children.Add(_time = Digits(Display, 250, FontWeights.SemiBold));
                stack.Children.Add(_day = new UniformGrid { Rows = 1, Width = DesignWidth * 0.74, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -6, 0, 0) });
                break;
        }
        return new Viewbox { Child = stack, Stretch = Stretch.Uniform };
    }

    private static TextBlock Line(FontFamily font, double size, FontWeight weight, Thickness margin) => new()
    {
        FontFamily = font,
        FontSize = size,
        FontWeight = weight,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = margin
    };

    private static TextBlock Digits(FontFamily font, double size, FontWeight weight, double lineHeight = 1.02)
    {
        var block = new TextBlock
        {
            FontFamily = font,
            FontSize = size,
            FontWeight = weight,
            HorizontalAlignment = HorizontalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = size * lineHeight
        };
        block.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        return block;
    }

    public override void Tick()
    {
        var now = DateTime.Now;
        var key = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + Style + ColorId + LanguageId;
        if (key == _shown || _date is null) return;
        _shown = key;
        var culture = DateCulture();
        var turkish = culture.Name.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        var english = culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        // Day and month in the language's own order ("19 November", "November 19", "11月19日").
        var monthDay = now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);
        PickColours();

        var solid = Brush(0xF2);
        var digits = DigitsBrush();
        _date.Foreground = solid;
        _date.Text = Style switch
        {
            // "19.Kas.2026"
            // Languages that write months as numbers ("11月") get "19.11.2026".
            "glass" => culture.DateTimeFormat.GetAbbreviatedMonthName(now.Month) is var month && month.Any(char.IsDigit)
                ? now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                : $"{now:dd}.{month.TrimEnd('.')}.{now:yyyy}",
            // The weekday is already beside it: the language's long date without it.
            "split" => turkish ? now.ToString("d MMMM yyyy", culture) : english ? now.ToString("MMMM d, yyyy", culture)
                : now.ToString(culture.DateTimeFormat.LongDatePattern.Replace("dddd", "").Trim(' ', ','), culture),
            "mono" => culture.TextInfo.ToLower(now.ToString("dddd", culture)) + " · " + now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            _ => turkish ? now.ToString("dddd d MMMM", culture) : english ? now.ToString("dddd, MMMM d", culture) : now.ToString("dddd", culture) + ", " + monthDay
        };
        if (_side is not null)
        {
            _side.Text = now.ToString("dddd", culture);
            _side.Foreground = solid;
            if (_side.Parent is StackPanel { Parent: Border bar }) bar.BorderBrush = Brush(0xB0);
        }

        if (_time is not null)
        {
            _time.Effect = null;
            if (_minutes is not null)
            {
                _time.Text = now.ToString("HH", culture);
                _minutes.Text = now.ToString("mm", culture);
                _minutes.Foreground = digits;
            }
            else if (Style == "twotone")
            {
                // Hours in the colour, minutes in white (black over a light picture), the colon faint between them.
                _time.Inlines.Clear();
                var other = new SolidColorBrush(Luminance(_tint) > 0.75 ? Rgb(0x1C, 0x1C, 0x1E) : Colors.White);
                _time.Inlines.Add(new Run(now.ToString("HH", culture)) { Foreground = digits });
                _time.Inlines.Add(new Run(":") { Foreground = Brush(0x80) });
                _time.Inlines.Add(new Run(now.ToString("mm", culture)) { Foreground = other });
            }
            else _time.Text = now.ToString("HH:mm", culture);
            if (Style != "twotone") _time.Foreground = digits;
            if (Style == "neon")
            {
                // A pale core with the colour glowing round it.
                _time.Foreground = new SolidColorBrush(Mix(_tint, Colors.White, 0.55));
                _time.Effect = new DropShadowEffect { Color = _tint, BlurRadius = 46, ShadowDepth = 0, Opacity = 1, RenderingBias = RenderingBias.Performance };
                _date.Effect = new DropShadowEffect { Color = _tint, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.9, RenderingBias = RenderingBias.Performance };
            }
        }

        if (_outline is not null)
        {
            var text = new FormattedText(now.ToString("HH:mm", culture), culture, FlowDirection.LeftToRight,
                new Typeface(Display, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 270, Brushes.White, 1.0);
            var geometry = text.BuildGeometry(new Point(0, 0));
            geometry.Freeze();
            _outline.Data = geometry;
            _outline.Stroke = digits;
            _outline.Fill = Brush(0x14);
            _outline.Margin = new Thickness(0, -geometry.Bounds.Top + 4, 0, 0);
        }

        if (_words is not null)
        {
            var lines = turkish ? TurkishWords(now) : EnglishWords(now);
            _words.Inlines.Clear();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) _words.Inlines.Add(new LineBreak());
                // The last line (geçiyor / var) quieter, as the phrase settles.
                _words.Inlines.Add(new Run(lines[i]) { Foreground = i == lines.Count - 1 && lines.Count > 1 ? Brush(0x99) : digits });
            }
        }

        if (_day is null) return;
        // The weekday in capitals, its letters spread evenly under the numbers.
        var day = culture.TextInfo.ToUpper(now.ToString("dddd", culture));
        _day.Children.Clear();
        var letters = Brush(0xE6);
        foreach (var letter in day)
            _day.Children.Add(new TextBlock { Text = letter.ToString(), FontFamily = Thin, FontSize = 64, Foreground = letters, HorizontalAlignment = HorizontalAlignment.Center });
    }

    private void PickColours()
    {
        _blend = null;
        if (ColorId is { } id && Palette.FirstOrDefault(p => p.Id == id) is { Id: not null } chosen)
        {
            _tint = chosen.From;
            _blend = chosen.To;
            return;
        }
        _tint = WallpaperTint.For(Area()) is { } tint ? tint : Colors.White;
    }

    private SolidColorBrush Brush(byte alpha) => new(Color.FromArgb(alpha, _tint.R, _tint.G, _tint.B));

    /// <summary>The numbers: a blend runs across them; the glass style fades its colour towards the bottom.</summary>
    private Brush DigitsBrush()
    {
        if (_blend is { } to)
            return Style == "glass"
                ? new LinearGradientBrush(Color.FromArgb(0xF7, _tint.R, _tint.G, _tint.B), Color.FromArgb(0x88, to.R, to.G, to.B), 90)
                : new LinearGradientBrush(_tint, to, new Point(0, 0), new Point(1, 1));
        return Style == "glass"
            ? new LinearGradientBrush(Color.FromArgb(0xF7, _tint.R, _tint.G, _tint.B), Color.FromArgb(0x66, _tint.R, _tint.G, _tint.B), 90)
            : Brush(0xF2);
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));

    // ---- The time in words -----------------------------------------------------------------------

    private static readonly string[] HourTr = ["on iki", "bir", "iki", "üç", "dört", "beş", "altı", "yedi", "sekiz", "dokuz", "on", "on bir", "on iki"];
    // "…i geçiyor" and "…e var": the hour with its ending.
    private static readonly string[] HourPastTr = ["on ikiyi", "biri", "ikiyi", "üçü", "dördü", "beşi", "altıyı", "yediyi", "sekizi", "dokuzu", "onu", "on biri", "on ikiyi"];
    private static readonly string[] HourToTr = ["on ikiye", "bire", "ikiye", "üçe", "dörde", "beşe", "altıya", "yediye", "sekize", "dokuza", "ona", "on bire", "on ikiye"];
    private static readonly string[] MinuteTr = ["", "beş", "on", "çeyrek", "yirmi", "yirmi beş"];

    /// <summary>"saat / on bir", "on biri / çeyrek / geçiyor", "on ikiye / beş / var" (to the nearest five minutes).</summary>
    private static List<string> TurkishWords(DateTime now)
    {
        var rounded = (int)Math.Round(now.Minute / 5.0) * 5;
        var hour = now.Hour % 12;
        if (rounded == 60)
        {
            rounded = 0;
            hour = (hour + 1) % 12;
        }
        if (rounded == 0) return ["saat", HourTr[hour]];
        if (rounded == 30) return [HourTr[hour], "buçuk"];
        if (rounded < 30) return [HourPastTr[hour], MinuteTr[rounded / 5], "geçiyor"];
        return [HourToTr[(hour + 1) % 12], MinuteTr[(60 - rounded) / 5], "var"];
    }

    private static readonly string[] HourEn = ["twelve", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];
    private static readonly string[] MinuteEn = ["", "five", "ten", "quarter", "twenty", "twenty-five"];

    private static List<string> EnglishWords(DateTime now)
    {
        var rounded = (int)Math.Round(now.Minute / 5.0) * 5;
        var hour = now.Hour % 12;
        if (rounded == 60)
        {
            rounded = 0;
            hour = (hour + 1) % 12;
        }
        if (rounded == 0) return [HourEn[hour], "o'clock"];
        if (rounded == 30) return ["half past", HourEn[hour]];
        if (rounded < 30) return [MinuteEn[rounded / 5], "past", HourEn[hour]];
        return [MinuteEn[(60 - rounded) / 5], "to", HourEn[(hour + 1) % 12]];
    }

    /// <summary>Where the clock stands on the screen (the wallpaper behind it decides the colour).</summary>
    private Rect Area()
    {
        var size = Desk.Dimensions(Size);
        return new Rect(Host.Item.X, Host.Item.Y, size.Width, size.Height);
    }

    public override void AddMenuItems(ItemsControl menu)
    {
        var style = new MenuItem { Header = Desk.L("Saat stili", "Clock style") };
        foreach (var (id, tr, en) in Styles)
            style.Items.Add(MenuEntry(Desk.L(tr, en), () =>
            {
                SetOption("style", id == "glass" ? null : id);
                Host.Rebuild();
            }, Style == id));
        menu.Items.Add(style);

        var color = new MenuItem { Header = Desk.L("Renk", "Colour") };
        color.Items.Add(MenuEntry(Desk.L("Duvar kâğıdından", "From the wallpaper"), () =>
        {
            SetOption("tint", null);
            Host.Rebuild();
        }, ColorId is null));
        color.Items.Add(new Separator());
        foreach (var (id, tr, en, from, to) in Palette)
        {
            if (id == "sunset") color.Items.Add(new Separator());
            var item = MenuEntry(Desk.L(tr, en), () =>
            {
                SetOption("tint", id);
                Host.Rebuild();
            }, ColorId == id);
            // A swatch of the colour (or the blend) beside its name.
            item.Icon = new Ellipse
            {
                Width = 14,
                Height = 14,
                Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)),
                StrokeThickness = 1,
                Fill = to is { } end ? new LinearGradientBrush(from, end, new Point(0, 0), new Point(1, 1)) : new SolidColorBrush(from)
            };
            color.Items.Add(item);
        }
        menu.Items.Add(color);

        var language = new MenuItem { Header = Desk.L("Tarih dili", "Date language") };
        language.Items.Add(MenuEntry(Desk.L("Uygulama dili", "App language"), () =>
        {
            SetOption("lang", null);
            Host.Rebuild();
        }, LanguageId is null));
        language.Items.Add(new Separator());
        foreach (var (code, name) in Languages)
            language.Items.Add(MenuEntry(name, () =>
            {
                SetOption("lang", code);
                Host.Rebuild();
            }, LanguageId == code));
        menu.Items.Add(language);
    }
}
