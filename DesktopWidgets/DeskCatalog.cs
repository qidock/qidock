using QiDock.DesktopWidgets.Kinds;
using static QiDock.DesktopWidgets.DeskSize;

namespace QiDock.DesktopWidgets;

/// <summary>Every desktop widget kind with its names, sizes and factory.</summary>
internal static class DeskCatalog
{
    public static readonly IReadOnlyList<DeskKind> All =
    [
        new("clock", DeskCategory.Time, "Dijital saat", "Digital clock", "Saat, gün ve tarih; orta boyda hava durumu da var.", "Time, weekday and date; the medium size adds the weather.",
            [Mini, Small, Strip, Medium], Small, () => new DigitalClockWidget()),
        new("analog", DeskCategory.Time, "Analog saat", "Analog clock", "Akrep, yelkovan ve saniye; açık ya da koyu kadran.", "Hour, minute and second hands; light or dark face.",
            [Small, Large], Small, () => new AnalogClockWidget()),
        new("flip", DeskCategory.Time, "Rakam kartları", "Flip clock", "Saat ve dakika büyük kartlarda.", "Hours and minutes on large tiles.",
            [Strip, Medium], Strip, () => new FlipClockWidget()),
        new("date", DeskCategory.Time, "Tarih", "Date", "Takvim yaprağı gibi bugünün tarihi.", "Today's date like a calendar page.",
            [Mini, Small, Strip], Small, () => new DateWidget()),
        new("calendar", DeskCategory.Time, "Takvim", "Calendar", "Bu hafta, ay görünümü ve iCal takviminizdeki etkinlikler.", "This week, the month and events from your iCal calendar.",
            [Small, Strip, Medium, Large], Medium, () => new CalendarDeskWidget()),
        new("progress", DeskCategory.Time, "Yıl ilerlemesi", "Year progress", "Yılın, ayın, haftanın ya da günün ne kadarı geçti.", "How much of the year, month, week or day has passed.",
            [Small, Strip, Medium], Strip, () => new ProgressWidget()),
        new("worldclock", DeskCategory.Time, "Dünya saati", "World clock", "Üç şehrin saati ve farkı.", "Three cities' time and offset.",
            [Small, Strip, Medium], Strip, () => new WorldClockDeskWidget()),
        new("timer", DeskCategory.Time, "Zamanlayıcı", "Timer", "Geri sayım; süre dolunca haber verir.", "Countdown that tells you when time is up.",
            [Mini, Small, Strip], Strip, () => new TimerWidget()),
        new("stopwatch", DeskCategory.Time, "Kronometre", "Stopwatch", "Saniyenin onda biri hassasiyetinde.", "Down to a tenth of a second.",
            [Mini, Strip], Strip, () => new StopwatchWidget()),

        new("weather", DeskCategory.Weather, "Hava durumu", "Weather", "Şu an, saatlik ve günlük tahmin (Ayarlar'daki şehir).", "Now, hourly and daily forecast (city from Settings).",
            [Mini, Small, Strip, Medium, Large], Medium, () => new WeatherDeskWidget()),
        new("sun", DeskCategory.Weather, "Gün doğumu ve batımı", "Sunrise & sunset", "Güneşin gökyüzündeki yeri ve gün uzunluğu.", "The sun's place in the sky and day length.",
            [Small, Strip], Strip, () => new SunWidget()),
        new("wind", DeskCategory.Weather, "Rüzgâr", "Wind", "Rüzgâr hızı, hamle ve yön pusulası.", "Wind speed, gusts and a direction compass.",
            [Small, Strip], Strip, () => new WindWidget()),

        new("cpu", DeskCategory.System, "İşlemci", "CPU", "İşlemci yükü halka ya da canlı grafik olarak.", "CPU load as a ring or a live graph.",
            [Mini, Small, Strip, Medium], Strip, () => new MetricWidget(Metric.Cpu)),
        new("ram", DeskCategory.System, "Bellek", "Memory", "Kullanılan RAM.", "Memory in use.",
            [Mini, Small, Strip], Mini, () => new MetricWidget(Metric.Ram)),
        new("disk", DeskCategory.System, "Disk", "Disk", "Sistem diskinin doluluğu ve boş alan.", "System drive usage and free space.",
            [Mini, Small, Strip], Mini, () => new MetricWidget(Metric.Disk)),
        new("battery", DeskCategory.System, "Pil", "Battery", "Pil yüzdesi, şarj durumu ve kalan süre.", "Battery level, charging and time left.",
            [Mini, Small, Strip], Mini, () => new MetricWidget(Metric.Battery)),
        new("network", DeskCategory.System, "Ağ hızı", "Network", "İndirme ve yükleme hızı, canlı grafik.", "Download and upload speed with a live graph.",
            [Mini, Strip, Medium], Strip, () => new NetworkWidget()),

        new("music", DeskCategory.Media, "Müzik", "Music", "Çalan şarkı, kapak ve kontroller (Spotify, tarayıcı…).", "Now playing, artwork and controls (Spotify, browsers…).",
            [Mini, Small, Strip, Medium, Large], Strip, () => new MusicWidget()),
        new("sound", DeskCategory.Media, "Ses", "Sound", "Ses düzeyi ve sessize alma.", "Volume level and mute.",
            [Mini, Strip], Strip, () => new SoundWidget()),

        new("todo", DeskCategory.Productivity, "Yapılacaklar", "To-do", "Üst çubuktaki listeyle aynı; işaretle, ekle, sil.", "Same list as the bar; check, add, remove.",
            [Strip, Small, Medium, Large], Small, () => new TodoDeskWidget()),
        new("notes", DeskCategory.Productivity, "Not", "Note", "Yapışkan not; yazdığınız saklanır.", "Sticky note; your text is kept.",
            [Small, Medium, Large], Small, () => new NotesWidget()),
        new("focus", DeskCategory.Productivity, "Odak (Pomodoro)", "Focus (Pomodoro)", "Üst çubuktaki odak sayacıyla aynı; 5 dk mola, 4 turda uzun mola.", "Same timer as the bar; 5 min breaks, a long one every 4 rounds.",
            [Small, Strip], Strip, () => new FocusDeskWidget()),
        new("reminder", DeskCategory.Productivity, "Hatırlatıcı", "Reminder", "Su iç, mola ver, gözlerini dinlendir… belirli aralıklarla.", "Drink water, take a break, rest your eyes… on a schedule.",
            [Small, Strip], Strip, () => new ReminderWidget()),

        new("claude", DeskCategory.Ai, "Claude", "Claude", "Claude kullanım sınırları (oturum ve haftalık) ve ne zaman yenileneceği; simgesi uygulamayı açar.", "Claude usage limits (session and weekly) and when they reset; the icon opens the app.",
            [Mini, Small, Strip, Medium], Strip, () => new AiAssistantWidget(AiAssistant.Claude)),
        new("codex", DeskCategory.Ai, "Codex", "Codex", "Codex (ChatGPT) kullanım sınırları ve ne zaman yenileneceği; simgesi uygulamayı açar.", "Codex (ChatGPT) usage limits and when they reset; the icon opens the app.",
            [Mini, Small, Strip, Medium], Strip, () => new AiAssistantWidget(AiAssistant.Codex)),
        new("gemini", DeskCategory.Ai, "Gemini", "Gemini", "Gemini uygulamasını tek tıkla açar.", "Opens the Gemini app in one click.",
            [Mini, Small, Strip, Medium], Strip, () => new AiAssistantWidget(AiAssistant.Gemini)),

        new("apps", DeskCategory.Tools, "Uygulamalar", "Apps", "Sık kullandığınız uygulamalar tek tıkla.", "Your favourite apps one click away.",
            [Mini, Small, Strip, Medium, Large], Small, () => new AppsWidget()),
        new("actions", DeskCategory.Tools, "Hızlı eylemler", "Quick actions", "Ekran alıntısı, pano, emoji, hesap makinesi, kilit…", "Screenshot, clipboard, emoji, calculator, lock…",
            [Mini, Small, Strip, Medium], Strip, () => new ActionsWidget()),
        new("toggles", DeskCategory.Tools, "Hızlı ayarlar", "Quick settings", "Wi-Fi, Bluetooth, gece ışığı, bildirimler.", "Wi-Fi, Bluetooth, night light, notifications.",
            [Small, Strip], Strip, () => new TogglesWidget()),
        new("websearch", DeskCategory.Tools, "Web'de ara", "Web search", "Yazıp Enter'a basın; varsayılan tarayıcıda açılır.", "Type and press Enter; opens in your browser.",
            [Strip], Strip, () => new SearchBoxWidget(true)),
        new("filesearch", DeskCategory.Tools, "Windows’ta ara", "Windows search", "Kullanıcı klasörünüzde Windows aramasıyla.", "Windows search in your user folder.",
            [Strip], Strip, () => new SearchBoxWidget(false)),
        new("emoji", DeskCategory.Tools, "Emoji", "Emoji", "Sık kullanılan emojiler; tıklayınca kopyalanır.", "Favourite emoji; click to copy.",
            [Small, Strip], Strip, () => new EmojiWidget()),
        new("colors", DeskCategory.Tools, "Renkler", "Colors", "Ekrandan renk seçin; tıklayınca kodu kopyalanır.", "Pick colors on screen; click to copy the code.",
            [Small, Strip], Small, () => new ColorsDeskWidget()),
        new("currency", DeskCategory.Tools, "Döviz çevirici", "Currency", "Avrupa Merkez Bankası kurlarıyla çeviri.", "Conversion with European Central Bank rates.",
            [Small, Strip], Strip, () => new CurrencyWidget()),
    ];

    public static DeskKind? Find(string id) => All.FirstOrDefault(k => k.Id == id);
}
