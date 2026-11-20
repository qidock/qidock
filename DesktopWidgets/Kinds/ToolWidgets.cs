using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QiDock.Services.Widgets;
using QiDock.Widgets;
using static QiDock.DesktopWidgets.DeskUi;

namespace QiDock.DesktopWidgets.Kinds;

/// <summary>App launcher: the dock's pinned apps by default, or apps you pick.</summary>
internal sealed class AppsWidget : DeskWidget
{
    private List<string> Apps
    {
        get
        {
            var own = Option("apps");
            var list = string.IsNullOrWhiteSpace(own) ? Desk.DockApps().ToList() : own.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            return list.Where(p => File.Exists(p) || Directory.Exists(p) || p.Contains("://", StringComparison.Ordinal)).ToList();
        }
    }

    public override FrameworkElement Build()
    {
        var (columns, rows, icon) = Size switch
        {
            DeskSize.Mini => (3, 1, 40),
            DeskSize.Small => (2, 2, 52),
            DeskSize.Strip => (6, 1, 44),
            DeskSize.Medium => (5, 2, 50),
            DeskSize.Tall => (2, 4, 54),
            _ => (4, 4, 54)
        };
        var apps = Apps.Take(columns * rows).ToList();
        if (apps.Count == 0) return Empty("IconApps", Desk.L("Sağ tık → Uygulama ekle", "Right-click → Add app"));
        var grid = new UniformGrid { Columns = columns, Rows = rows };
        foreach (var path in apps)
        {
            var image = Ui.FileIcon(path, icon);
            var button = new DeskButton(image, () => Ui.Open(path), radius: 14)
            {
                ToolTip = Path.GetFileNameWithoutExtension(path),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(2)
            };
            grid.Children.Add(button);
        }
        return grid;
    }

    public override void AddMenuItems(ItemsControl menu)
    {
        menu.Items.Add(MenuEntry(Desk.L("Uygulama ekle…", "Add app…"), () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Desk.L("Eklenecek uygulama", "App to add"),
                Filter = Desk.L("Uygulamalar ve kısayollar", "Apps and shortcuts") + "|*.exe;*.lnk;*.url;*.appref-ms|" + Desk.L("Tüm dosyalar", "All files") + "|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Multiselect = true
            };
            if (dialog.ShowDialog() != true) return;
            var list = Apps;
            list.AddRange(dialog.FileNames.Where(f => !list.Contains(f, StringComparer.OrdinalIgnoreCase)));
            SetOption("apps", string.Join('\n', list));
            Host.Rebuild();
        }));
        if (Option("apps") is not null)
        {
            menu.Items.Add(MenuEntry(Desk.L("Son ekleneni kaldır", "Remove last added"), () =>
            {
                var list = Apps;
                if (list.Count > 0) list.RemoveAt(list.Count - 1);
                SetOption("apps", list.Count == 0 ? null : string.Join('\n', list));
                Host.Rebuild();
            }));
            menu.Items.Add(MenuEntry(Desk.L("Dock'taki uygulamaları göster", "Use the dock's apps"), () =>
            {
                SetOption("apps", null);
                Host.Rebuild();
            }));
        }
    }
}

/// <summary>One-click actions: screenshot, clipboard history, emoji, calculator, lock and more.</summary>
internal sealed class ActionsWidget : DeskWidget
{
    private static readonly (string Icon, string Tr, string En, string Color, Action Run)[] Actions =
    [
        ("IconCamera", "Ekran alıntısı", "Snip", "#FF0A84FF", () => Ui.Open("ms-screenclip:")),
        ("IconClipboard", "Pano geçmişi", "Clipboard history", "#FF30D158", () => Desk.SendWinKey(0x56)),
        ("IconSmile", "Emoji", "Emoji", "#FFFF9F0A", () => Desk.SendWinKey(0xBE)),
        ("IconCalculator", "Hesap makinesi", "Calculator", "#FFBF5AF2", () => Ui.Open("calc.exe")),
        ("IconLock", "Ekranı kilitle", "Lock screen", "#FFFF453A", () => Desk.LockWorkStation()),
        ("IconFolder", "Dosya Gezgini", "File Explorer", "#FF64D2FF", () => Ui.Open("explorer.exe")),
        ("IconActivity", "Görev Yöneticisi", "Task Manager", "#FFFF375F", () => Ui.Open("taskmgr.exe")),
        ("IconSettings", "Ayarlar", "Settings", "#FF8E8E93", () => Ui.Open("ms-settings:"))
    ];

    public override FrameworkElement Build()
    {
        var (count, columns, size) = Size switch
        {
            DeskSize.Mini => (3, 3, 40),
            DeskSize.Small => (4, 2, 48),
            DeskSize.Strip => (6, 6, 42),
            DeskSize.Tall => (8, 2, 50),
            _ => (8, 4, 50)
        };
        var grid = new UniformGrid { Columns = columns };
        foreach (var action in Actions.Take(count))
        {
            var color = Ui.Solid(action.Color);
            var tint = (SolidColorBrush)Ui.Solid("#33" + action.Color[3..]);
            var button = new DeskButton(DeskUi.Icon(action.Icon, size * 0.4, color), action.Run, size, tint)
            {
                ToolTip = Desk.L(action.Tr, action.En),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(button);
        }
        return grid;
    }
}

/// <summary>Search box: the web (default browser) or files (Windows search).</summary>
internal sealed class SearchBoxWidget(bool web) : DeskWidget
{
    private static readonly (string Key, string Name, string Url)[] Engines =
    [
        ("google", "Google", "https://www.google.com/search?q="),
        ("bing", "Bing", "https://www.bing.com/search?q="),
        ("duck", "DuckDuckGo", "https://duckduckgo.com/?q="),
        ("youtube", "YouTube", "https://www.youtube.com/results?search_query=")
    ];

    private (string Key, string Name, string Url) Engine => Engines.FirstOrDefault(e => e.Key == (Option("engine") ?? "google")) is { Key: not null } e ? e : Engines[0];

    public override FrameworkElement Build()
    {
        var hint = web ? Desk.L($"{Engine.Name} ile ara…", $"Search {Engine.Name}…") : Desk.L("Dosya ara…", "Find files…");
        var box = DeskInput.Box(hint, 14);
        DeskInput.Focusable(box, Host);
        void Go()
        {
            var query = box.Text.Trim();
            if (query.Length == 0) return;
            if (web) Ui.Open(Engine.Url + Uri.EscapeDataString(query));
            else Ui.Open("search-ms:query=" + Uri.EscapeDataString(query) + "&crumb=location:" + Uri.EscapeDataString(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            box.Clear();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Go();
            if (e.Key == Key.Escape) box.Clear();
        };
        var go = DeskButton.Round("IconArrowRight", Go, 28, InsetHover, Text, outline: true, glyph: 13);
        var grid = Grid(columns: "Auto,*,Auto");
        grid.Children.Add(DeskUi.Icon(web ? "IconSearch" : "IconFolder", 16, Secondary));
        grid.Children.Add(At(Margin(DeskInput.WithHint(box), 10, 0, 8), 0, 1));
        grid.Children.Add(At(go, 0, 2));
        var field = DeskInput.Field(grid, 46);
        field.Padding = new Thickness(16, 0, 9, 0);
        field.CornerRadius = new CornerRadius(16);
        field.VerticalAlignment = VerticalAlignment.Center;
        return field;
    }

    public override void AddMenuItems(ItemsControl menu)
    {
        if (!web) return;
        var engines = new MenuItem { Header = Desk.L("Arama motoru", "Search engine") };
        foreach (var engine in Engines)
            engines.Items.Add(MenuEntry(engine.Name, () =>
            {
                SetOption("engine", engine.Key == "google" ? null : engine.Key);
                Host.Rebuild();
            }, Engine.Key == engine.Key));
        menu.Items.Add(engines);
    }
}

/// <summary>Favourite emoji: a click copies one; + opens the Windows emoji panel.</summary>
internal sealed class EmojiWidget : DeskWidget
{
    private const string Defaults = "😂 ✨ ✅ 🔥 🥹 🙏 💡 🚀 ❤️ 👍 🎉 😊 🤔 👀 💯 😍";

    public override FrameworkElement Build()
    {
        var emoji = (Option("set") ?? Defaults).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (columns, rows) = Size == DeskSize.Small ? (4, 4) : (5, 2);
        var grid = new UniformGrid { Columns = columns, Rows = rows };
        foreach (var e in emoji.Take(columns * rows - 1))
        {
            var text = new TextBlock { Text = e, FontSize = Size == DeskSize.Small ? 20 : 19, FontFamily = new FontFamily("Segoe UI Emoji"), Foreground = DeskUi.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            TextOptions.SetTextRenderingMode(text, TextRenderingMode.Grayscale);
            grid.Children.Add(new DeskButton(text, () => Copy(e), radius: 8));
        }
        grid.Children.Add(new DeskButton(DeskUi.Icon("IconPlus", 15, Secondary), () => Desk.SendWinKey(0xBE), radius: 8) { ToolTip = Desk.L("Emoji paneli", "Emoji panel") });
        return grid;
    }

    private static void Copy(string emoji)
    {
        try
        {
            Clipboard.SetText(emoji);
            Ui.ShowHud("IconCopy", Desk.L($"{emoji} kopyalandı", $"{emoji} copied"));
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}

/// <summary>Saved colors (shared with the bar's color picker): click copies, the pipette picks a new one.</summary>
internal sealed class ColorsDeskWidget : DeskWidget
{
    public override FrameworkElement Build()
    {
        var colors = (Desk.Settings().Colors ?? []).ToList();
        var (columns, rows) = Size == DeskSize.Small ? (2, 2) : (6, 1);
        var grid = new UniformGrid { Columns = columns, Rows = rows };
        foreach (var hex in colors.Take(columns * rows - 1))
        {
            Brush fill;
            try { fill = Ui.Solid(hex); }
            catch (FormatException) { continue; }
            var swatch = new Border { CornerRadius = new CornerRadius(Size == DeskSize.Small ? 14 : 12), Background = fill, Margin = new Thickness(4), BorderBrush = CardStroke, BorderThickness = new Thickness(1) };
            var button = new DeskButton(swatch, () => Copy(hex), radius: 14) { ToolTip = hex };
            button.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                Desk.UpdateSettings(s => s with { Colors = (s.Colors ?? []).Where(c => !string.Equals(c, hex, StringComparison.OrdinalIgnoreCase)).ToList() });
                Host.Rebuild();
            };
            grid.Children.Add(button);
        }
        var pipette = new Border { CornerRadius = new CornerRadius(14), Background = Inset, Margin = new Thickness(4), Child = DeskUi.Icon("IconPipette", 18, Text) };
        grid.Children.Add(new DeskButton(pipette, Pick, radius: 14) { ToolTip = Desk.L("Ekrandan renk seç", "Pick a color on screen") });
        return grid;
    }

    private void Pick()
    {
        var overlay = new ColorPickerOverlay();
        overlay.Picked += hex =>
        {
            Desk.UpdateSettings(s => s with { Colors = new[] { hex }.Concat((s.Colors ?? []).Where(c => !string.Equals(c, hex, StringComparison.OrdinalIgnoreCase))).Take(24).ToList() });
            Copy(hex);
            Host.Rebuild();
        };
        overlay.Show();
    }

    private static void Copy(string hex)
    {
        try
        {
            Clipboard.SetText(hex);
            Ui.ShowHud("IconCopy", Desk.L($"{hex} kopyalandı", $"{hex} copied"));
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    public override void SettingsChanged() => Host.Rebuild();

    public override void AddMenuItems(ItemsControl menu)
    {
        menu.Items.Add(MenuEntry(Desk.L("Renk seç", "Pick a color"), Pick));
        menu.Items.Add(new MenuItem { Header = Desk.L("Tıkla: kopyala · Sağ tık: sil", "Click: copy · Right-click: remove"), IsEnabled = false });
    }
}

/// <summary>Currency converter with European Central Bank rates.</summary>
internal sealed class CurrencyWidget : DeskWidget
{
    private TextBox? _amount;
    private TextBlock? _result, _rate;
    private int _version;

    private string From => Option("from") ?? "USD";
    private string To => Option("to") ?? "TRY";

    public override FrameworkElement Build()
    {
        _amount = DeskInput.Box("0", 16);
        _amount.Text = Option("amount") ?? "100";
        _amount.FontWeight = FontWeights.SemiBold;
        DeskInput.Focusable(_amount, Host);
        _amount.TextChanged += (_, _) =>
        {
            Host.Item.Options["amount"] = _amount.Text;
            Host.Save();
            _ = UpdateAsync();
        };
        _result = Digits("", Size == DeskSize.Small ? 24 : 26, FontWeights.SemiBold);
        _rate = T("", 11, FontWeights.SemiBold, Secondary);
        var from = CurrencyButton(From, code => SetOption("from", code));
        var to = CurrencyButton(To, code => SetOption("to", code));
        var swap = new DeskButton(DeskUi.Icon("IconSwap", 13, Secondary), () =>
        {
            var (a, b) = (From, To);
            Host.Item.Options["from"] = b;
            Host.Item.Options["to"] = a;
            Host.Save();
            Host.Rebuild();
        }, 24);
        if (Size == DeskSize.Small)
        {
            var top = Grid(columns: "*,Auto");
            top.Children.Add(_amount);
            top.Children.Add(At(from, 0, 1));
            var bottom = Grid(columns: "*,Auto");
            bottom.Children.Add(_result);
            bottom.Children.Add(At(to, 0, 1));
            swap.HorizontalAlignment = HorizontalAlignment.Right;
            return Column(top, Margin(swap, 0, 4, 2, 4), bottom, Margin(_rate, 0, 6));
        }
        _result.FontSize = 18;
        _amount.VerticalAlignment = _result.VerticalAlignment = VerticalAlignment.Center;
        var left = Grid(columns: "*,Auto");
        left.Children.Add(_amount);
        left.Children.Add(At(from, 0, 1));
        var right = Grid(columns: "*,Auto");
        right.Children.Add(_result);
        right.Children.Add(At(to, 0, 1));
        var row = Grid(columns: "*,Auto,*");
        row.Children.Add(left);
        row.Children.Add(At(Margin(swap, 6, 0, 6, 0), 0, 1));
        row.Children.Add(At(Margin(right, 4, 0, 0, 0), 0, 2));
        var grid = Grid("*,Auto");
        grid.Children.Add(At(row, 0));
        grid.Children.Add(At(Margin(_rate, 0, 2), 1));
        return grid;
    }

    private DeskButton CurrencyButton(string code, Action<string> choose)
    {
        var label = T(code, 11.5, FontWeights.Bold);
        label.Margin = new Thickness(8, 0, 8, 0);
        var button = new DeskButton(label, background: InsetHover, radius: 9) { Height = 20 };
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (var currency in RatesHub.Currencies)
                menu.Items.Add(MenuEntry(currency, () =>
                {
                    choose(currency);
                    Host.Rebuild();
                }, currency == code));
            menu.IsOpen = true;
        };
        return button;
    }

    public override TimeSpan? Interval => TimeSpan.FromMinutes(30);
    protected override void OnAttach() => _ = UpdateAsync();
    public override void Tick() => _ = UpdateAsync();

    private async Task UpdateAsync()
    {
        var version = ++_version;
        if (_amount is null || _result is null || _rate is null) return;
        var text = _amount.Text.Replace(" ", "", StringComparison.Ordinal);
        if (!double.TryParse(text, NumberStyles.Number, Desk.Culture, out var amount) && !double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
        {
            _result.Text = "–";
            return;
        }
        var rate = await RatesHub.RateAsync(From, To);
        if (version != _version) return;
        if (rate is not { } value)
        {
            _result.Text = "–";
            _rate.Text = Desk.L("Kur alınamadı", "Rate unavailable");
            return;
        }
        _result.Text = (amount * value).ToString("N2", Desk.Culture);
        _rate.Text = $"1 {From} = {value.ToString("N4", Desk.Culture)} {To}";
    }
}

/// <summary>Output volume with mute, following changes made elsewhere.</summary>
internal sealed class SoundWidget : DeskWidget
{
    private readonly AudioEndpoint _endpoint = new();
    private Slider? _slider;
    private TextBlock? _percent, _device;
    private DeskButton? _mute;
    private bool _updating;

    public override TimeSpan? Interval => TimeSpan.FromSeconds(2);

    public override FrameworkElement Build()
    {
        _slider = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        _slider.SetResourceReference(FrameworkElement.StyleProperty, "WidgetSlider");
        _slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            _endpoint.SetVolume((float)(e.NewValue / 100));
            if (_percent is not null) _percent.Text = Percent(e.NewValue);
        };
        _slider.PreviewMouseWheel += (_, e) =>
        {
            _slider.Value = Math.Clamp(_slider.Value + Math.Sign(e.Delta) * 4, 0, 100);
            e.Handled = true;
        };
        _percent = Digits("", 12, FontWeights.SemiBold, Secondary);
        _percent.Width = 38;
        _percent.TextAlignment = TextAlignment.Right;
        _device = T("", 12, FontWeights.SemiBold, Secondary);
        _mute = new DeskButton(DeskUi.Icon("IconVolume", 16, Text), ToggleMute, 30, Inset);
        var row = Grid(columns: "Auto,*,Auto");
        row.Children.Add(_mute);
        row.Children.Add(At(Margin(_slider, 10, 0, 4), 0, 1));
        row.Children.Add(At(_percent, 0, 2));
        if (Size == DeskSize.Mini) return row;
        var settings = new DeskButton(DeskUi.Icon("IconSettings", 13, Secondary), () => Ui.Open("ms-settings:sound"), 24) { ToolTip = Desk.L("Ses ayarları", "Sound settings") };
        var header = Grid(columns: "*,Auto");
        header.Children.Add(_device);
        header.Children.Add(At(settings, 0, 1));
        var grid = Grid("Auto,*");
        grid.Children.Add(header);
        grid.Children.Add(At(Margin(row, 0, 4), 1));
        return grid;
    }

    public override void Tick()
    {
        if (_endpoint.Read() is not { } state || _slider is null) return;
        _updating = true;
        if (!_slider.IsMouseCaptureWithin) _slider.Value = state.Volume * 100;
        _updating = false;
        _percent!.Text = Percent(state.Volume * 100);
        if (_device is not null) _device.Text = state.DeviceName;
        _mute!.Child = DeskUi.Icon(state.Muted ? "IconVolumeMute" : "IconVolume", 16, state.Muted ? Red : Text);
    }

    private void ToggleMute()
    {
        if (_endpoint.Read() is { } state) _endpoint.SetMute(!state.Muted);
        Tick();
    }

    public override void Dispose()
    {
        _endpoint.Dispose();
        base.Dispose();
    }
}

/// <summary>Quick settings: Wi-Fi, Bluetooth, night light, focus, on-screen keyboard.</summary>
internal sealed class TogglesWidget : DeskWidget
{
    private static RadioControls? _radios;
    private DeskButton? _wifi, _bluetooth;

    public override FrameworkElement Build()
    {
        _wifi = Toggle("IconWifi", Blue, async () =>
        {
            if (_radios is not null) await _radios.ToggleWifiAsync();
            Tick();
        }, Desk.L("Wi-Fi", "Wi-Fi"));
        _bluetooth = Toggle("IconBluetooth", Blue, async () =>
        {
            if (_radios is not null) await _radios.ToggleBluetoothAsync();
            Tick();
        }, "Bluetooth");
        var night = Toggle("IconMoon", Orange, () => Ui.Open("ms-settings:nightlight"), Desk.L("Gece ışığı", "Night light"));
        var focus = Toggle("IconBell", Purple, () => Ui.Open("ms-settings:quiethours"), Desk.L("Bildirimler / odak", "Notifications / focus"));
        var keyboard = Toggle("IconKeyboard", Secondary, () => Ui.Open("osk.exe"), Desk.L("Ekran klavyesi", "On-screen keyboard"));
        if (Size == DeskSize.Tall)
        {
            // Upright: one switch per row with its name, as a phone's control centre.
            var list = new UniformGrid { Columns = 1, Rows = 5 };
            foreach (var (button, name) in new[]
            {
                (_wifi, "Wi-Fi"), (_bluetooth, "Bluetooth"), (night, Desk.L("Gece ışığı", "Night light")),
                (focus, Desk.L("Odak", "Focus")), (keyboard, Desk.L("Klavye", "Keyboard"))
            })
            {
                button.Width = button.Height = 44;
                button.HorizontalAlignment = HorizontalAlignment.Left;
                var row = Grid(columns: "Auto,*");
                row.Children.Add(button);
                row.Children.Add(At(Margin(T(name, 13, FontWeights.SemiBold), 11), 0, 1));
                list.Children.Add(row);
            }
            return list;
        }
        var grid = new UniformGrid { Columns = Size == DeskSize.Small ? 2 : 5, Rows = Size == DeskSize.Small ? 2 : 1 };
        grid.Children.Add(_wifi);
        grid.Children.Add(_bluetooth);
        grid.Children.Add(night);
        grid.Children.Add(focus);
        if (Size != DeskSize.Small) grid.Children.Add(keyboard);
        return grid;
    }

    private static DeskButton Toggle(string icon, Brush color, Action click, string tooltip)
    {
        var button = new DeskButton(DeskUi.Icon(icon, 18, Text), click, 48, Inset)
        {
            ToolTip = tooltip,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = color
        };
        return button;
    }

    protected override async void OnAttach()
    {
        if (_radios is null)
        {
            _radios = new RadioControls();
            try { await _radios.InitializeAsync(); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        _radios.Changed += RadiosChanged;
        Tick();
    }

    protected override void OnDetach()
    {
        if (_radios is not null) _radios.Changed -= RadiosChanged;
    }

    private void RadiosChanged(object? sender, EventArgs e) => Application.Current.Dispatcher.InvokeAsync(Tick);

    public override void Tick()
    {
        Paint(_wifi, _radios?.HasWifi == true, _radios?.WifiOn == true, "IconWifi", "IconWifiOff");
        Paint(_bluetooth, _radios?.HasBluetooth == true, _radios?.BluetoothOn == true, "IconBluetooth", "IconBluetoothOff");
    }

    private static void Paint(DeskButton? button, bool available, bool on, string onIcon, string offIcon)
    {
        if (button is null) return;
        var color = (Brush)button.Tag;
        button.Opacity = available ? 1 : 0.4;
        button.BorderBrush = on ? color : Brushes.Transparent;
        button.Child = DeskUi.Icon(on ? onIcon : offIcon, 18, on ? color : Secondary);
    }
}
