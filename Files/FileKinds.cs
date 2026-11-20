using Path = System.Windows.Shapes.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QiDock.Files;

/// <summary>What a file (or most of a folder) holds, as far as its extension tells.</summary>
internal enum FileKind { Other, Code, Image, Video, Audio, Document, Archive, App }

internal static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string list)
        {
            foreach (var extension in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map["." + extension] = kind;
        }
        Add(FileKind.Code, "cs csproj sln xaml js jsx ts tsx mjs cjs json py ipynb java kt kts go rs c h cpp hpp cc m mm swift rb php html htm css scss sass less vue svelte sql sh ps1 psm1 bat cmd yml yaml toml ini xml gradle lua dart r pl ex exs fs fsx vb md mdx editorconfig gitignore dockerfile");
        Add(FileKind.Image, "png jpg jpeg gif bmp webp heic heif tif tiff svg ico raw cr2 nef arw dng psd ai afphoto afdesign xcf avif jxl");
        Add(FileKind.Video, "mp4 mov mkv avi wmv webm m4v flv mpg mpeg 3gp mts m2ts drp braw");
        Add(FileKind.Audio, "mp3 wav flac aac m4a ogg opus wma aiff aif mid midi");
        Add(FileKind.Document, "pdf doc docx xls xlsx xlsm csv ppt pptx odt ods odp rtf txt log epub pages numbers key one msg eml");
        Add(FileKind.Archive, "zip rar 7z tar gz tgz bz2 xz iso img cab zst");
        Add(FileKind.App, "exe msi msix appx appinstaller lnk url dll sys");
        return map;
    }

    public static FileKind Of(string path) =>
        ByExtension.TryGetValue(System.IO.Path.GetExtension(path), out var kind) ? kind : FileKind.Other;

    /// <summary>The folder colour for what it mostly holds (an empty folder is grey).</summary>
    public static Color FolderColor(FileKind? kind) => kind switch
    {
        null => Color.FromRgb(0x8A, 0x8F, 0x9A),
        FileKind.Code => Color.FromRgb(0x7C, 0x5C, 0xF6),
        FileKind.Image => Color.FromRgb(0xE0, 0x56, 0x9B),
        FileKind.Video => Color.FromRgb(0xE5, 0x4D, 0x4D),
        FileKind.Audio => Color.FromRgb(0xF2, 0x8C, 0x28),
        FileKind.Document => Color.FromRgb(0x3E, 0x8C, 0xF0),
        FileKind.Archive => Color.FromRgb(0xA9, 0x7C, 0x50),
        FileKind.App => Color.FromRgb(0x2F, 0xB3, 0x7C),
        _ => Color.FromRgb(0x6A, 0x66, 0xEE)
    };

    public static string Name(FileKind kind) => kind switch
    {
        FileKind.Code => L("Kod", "Code"),
        FileKind.Image => L("Resim", "Image"),
        FileKind.Video => L("Video", "Video"),
        FileKind.Audio => L("Ses", "Audio"),
        FileKind.Document => L("Belge", "Document"),
        FileKind.Archive => L("Arşiv", "Archive"),
        FileKind.App => L("Uygulama", "App"),
        _ => L("Dosya", "File")
    };

    public static string L(string turkish, string english) => DesktopWidgets.Desk.L(turkish, english);

    // ---- Line icons (24×24, drawn as strokes) --------------------------------------------------

    public static readonly Geometry GlyphCode = Freeze("M16 18l6-6-6-6 M8 6l-6 6 6 6");
    public static readonly Geometry GlyphImage = Freeze("M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M8.5 7a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z M21 15l-5-5L5 21");
    public static readonly Geometry GlyphVideo = Freeze("M4 5h11a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2z M22 8l-5 4 5 4z");
    public static readonly Geometry GlyphAudio = Freeze("M9 18V5l12-2v13 M6 15a3 3 0 1 0 0 6a3 3 0 1 0 0-6z M18 13a3 3 0 1 0 0 6a3 3 0 1 0 0-6z");
    public static readonly Geometry GlyphDocument = Freeze("M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M16 13H8 M16 17H8 M10 9H8");
    public static readonly Geometry GlyphArchive = Freeze("M3 4h18v4H3z M5 8v11a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8 M10 12h4");
    public static readonly Geometry GlyphApp = Freeze("M3 3h7v7H3z M14 3h7v7h-7z M14 14h7v7h-7z M3 14h7v7H3z");
    public static readonly Geometry GlyphBox = Freeze("M3 7h18v13a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z M5 3h14l2 4H3z M10 11h4");
    public static readonly Geometry GlyphDownload = Freeze("M12 3v12 M7 10l5 5 5-5 M5 21h14");
    public static readonly Geometry GlyphDesktop = Freeze("M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M8 21h8 M12 17v4");
    public static readonly Geometry GlyphHome = Freeze("M3 10l9-7 9 7v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M9 22V12h6v10");
    public static readonly Geometry GlyphCloud = Freeze("M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9z");
    public static readonly Geometry GlyphDrive = Freeze("M22 12H2 M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z M6 16h.01 M10 16h.01");
    public static readonly Geometry GlyphUsb = Freeze("M7 2h10v6H7z M5 8h14v12a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2z M10 4v2 M14 4v2");
    public static readonly Geometry GlyphTrash = Freeze("M3 6h18 M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6 M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2");
    public static readonly Geometry GlyphFolder = Freeze("M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.93a2 2 0 0 1-1.66-.9l-.82-1.2A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13c0 1.1.9 2 2 2z");
    public static readonly Geometry GlyphList = Freeze("M8 6h13 M8 12h13 M8 18h13 M3 6h.01 M3 12h.01 M3 18h.01");
    public static readonly Geometry GlyphGrid = Freeze("M3 3h7v7H3z M14 3h7v7h-7z M14 14h7v7h-7z M3 14h7v7H3z");
    public static readonly Geometry GlyphPanel = Freeze("M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M15 3v18");
    public static readonly Geometry GlyphSort = Freeze("M3 6h18 M6 12h12 M10 18h4");
    public static readonly Geometry GlyphStar = Freeze("M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01z");
    public static readonly Geometry GlyphEye = Freeze("M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7z M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6z");

    public static Geometry Glyph(FileKind kind) => kind switch
    {
        FileKind.Code => GlyphCode,
        FileKind.Image => GlyphImage,
        FileKind.Video => GlyphVideo,
        FileKind.Audio => GlyphAudio,
        FileKind.Document => GlyphDocument,
        FileKind.Archive => GlyphArchive,
        FileKind.App => GlyphApp,
        _ => GlyphBox
    };

    private static Geometry Freeze(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    public static Path Line(Geometry geometry, double size, Brush stroke, double thickness = 1.8) => new()
    {
        Data = geometry,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Stroke = stroke,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        SnapsToDevicePixels = true,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var culture = UiTextCulture;
        return unit == 0 ? $"{bytes} {units[0]}" : value.ToString(value >= 100 ? "0" : "0.#", culture) + " " + units[unit];
    }

    private static System.Globalization.CultureInfo UiTextCulture =>
        Services.UiText.Current.Language == Models.AppLanguage.Turkish ? System.Globalization.CultureInfo.GetCultureInfo("tr-TR") : System.Globalization.CultureInfo.InvariantCulture;

    public static string Date(DateTime time)
    {
        var culture = UiTextCulture;
        if (time.Date == DateTime.Today) return L("Bugün ", "Today ") + time.ToString("HH:mm", culture);
        if (time.Date == DateTime.Today.AddDays(-1)) return L("Dün ", "Yesterday ") + time.ToString("HH:mm", culture);
        return time.ToString(time.Year == DateTime.Today.Year ? "d MMM HH:mm" : "d MMM yyyy", culture);
    }
}

/// <summary>
/// The colourful folder picture: a darker back with its tab, a sheet of paper peeking out and a lighter front
/// carrying the item count and a line icon for what the folder mostly holds.
/// </summary>
internal static class FolderArt
{
    private static readonly Geometry Back = Geometry.Parse("M10 22 Q10 12 20 12 H44 Q50 12 54 17 L58 22 H100 Q110 22 110 32 V86 Q110 96 100 96 H20 Q10 96 10 86 Z");
    private static readonly Geometry Front = Geometry.Parse("M10 46 Q10 38 18 38 H102 Q110 38 110 46 V86 Q110 96 100 96 H20 Q10 96 10 86 Z");
    private static readonly Geometry Paper = Geometry.Parse("M18 26 H102 Q104 26 104 28 V60 H16 V28 Q16 26 18 26 Z");
    private static readonly Dictionary<Color, (Brush Back, Brush Front, Brush Edge)> Brushes = [];
    private static readonly Brush PaperFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));

    static FolderArt()
    {
        Back.Freeze();
        Front.Freeze();
        Paper.Freeze();
    }

    public static FrameworkElement Create(Color color, string? count, Geometry glyph, double width)
    {
        var (back, front, edge) = BrushesFor(color);
        var canvas = new Canvas { Width = 120, Height = 104 };
        canvas.Children.Add(new Path { Data = Back, Fill = back });
        canvas.Children.Add(new Path { Data = Paper, Fill = PaperFill, RenderTransform = new RotateTransform(-1.5, 60, 40) });
        canvas.Children.Add(new Path { Data = Front, Fill = front, Stroke = edge, StrokeThickness = 0.8 });
        if (count is not null)
        {
            var label = new TextBlock { Text = count, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Ink };
            Canvas.SetLeft(label, 18);
            Canvas.SetTop(label, 44);
            canvas.Children.Add(label);
        }
        var icon = FileKinds.Line(glyph, 22, Ink, 1.7);
        Canvas.SetLeft(icon, 49);
        Canvas.SetTop(icon, 63);
        canvas.Children.Add(icon);
        return new Viewbox { Width = width, Height = width * 104 / 120, Child = canvas, Stretch = Stretch.Uniform };
    }

    private static (Brush, Brush, Brush) BrushesFor(Color color)
    {
        if (Brushes.TryGetValue(color, out var cached)) return cached;
        var back = Frozen(new SolidColorBrush(Shade(color, 0.72)));
        var front = Frozen(new LinearGradientBrush(Shade(color, 1.18), color, 90));
        var edge = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
        return Brushes[color] = (back, front, edge);
    }

    private static Color Shade(Color color, double factor)
    {
        static byte Channel(byte value, double factor) => (byte)Math.Clamp(factor >= 1 ? value + (255 - value) * (factor - 1) : value * factor, 0, 255);
        return Color.FromRgb(Channel(color.R, factor), Channel(color.G, factor), Channel(color.B, factor));
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
