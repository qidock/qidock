using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// Currency converter: the tile shows one unit of the first currency in the second (1 USD = 34,21 ₺); the card
/// converts any amount. Rates are the European Central Bank's daily reference rates (frankfurter.app, no key, no
/// personal data sent), fetched at most every six hours and only while the widget is shown.
/// </summary>
public sealed class CurrencyWidget : DockWidget
{
    private static readonly string[] Codes = ["TRY", "USD", "EUR", "GBP", "CHF", "JPY"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);

    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly TextBlock _pair;
    private readonly TextBlock _rate;
    private readonly TextBox _amount = new() { Text = "100" };
    private readonly TextBlock _result = Ui.Text("", 26, FontWeights.Bold);
    private readonly TextBlock _status = Ui.Text("", 10.5, brushKey: "TextSecondary");
    private readonly Button _fromButton;
    private readonly Button _toButton;
    private readonly System.Windows.Threading.DispatcherTimer _refresh = new() { Interval = TimeSpan.FromHours(1) };
    // Rates against the euro (the bank's base): 1 EUR = rate[code].
    private Dictionary<string, double> _rates = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _fetchedAt;
    private string? _rateDate;
    private bool _loading;
    private bool _started;
    private StackPanel? _card;

    public CurrencyWidget()
    {
        var icon = Ui.Icon("IconSwap", 14);
        icon.Margin = new Thickness(0, 0, 8, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _pair = Ui.Text("", 10, brushKey: "TextSecondary");
        _rate = Ui.Text("…", 13.5, FontWeights.Bold);
        _rate.FontFeatures();
        text.Children.Add(_pair);
        text.Children.Add(_rate);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (_, _) => Toggle());
        _popup = new WidgetPopup(_tile);

        _amount.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTextBox");
        _amount.FontSize = 18;
        _amount.TextChanged += (_, _) => RenderCard();
        _amount.KeyDown += (_, e) => { if (e.Key == Key.Escape) _popup.Close(); };
        _result.FontFeatures();
        _result.Margin = new Thickness(2, 10, 0, 2);
        _fromButton = Ui.Pill("", (_, _) => Cycle(from: true));
        _toButton = Ui.Pill("", (_, _) => Cycle(from: false));
        _refresh.Tick += (_, _) => _ = LoadAsync();
        UiText.Current.PropertyChanged += (_, _) => Render();
    }

    public override string Id => WidgetIds.Currency;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    public override void Start()
    {
        _started = true;
        _refresh.Start();
        _ = LoadAsync();
    }

    public override void Stop()
    {
        _started = false;
        _refresh.Stop();
        _popup.Close();
    }

    public override void ApplySettings(WidgetSettings settings) => Render();

    private async Task LoadAsync()
    {
        if (_loading || !_started || DateTime.UtcNow - _fetchedAt < Fresh && _rates.Count > 0) return;
        _loading = true;
        try
        {
            using var stream = await Http.GetStreamAsync("https://api.frankfurter.app/latest?from=EUR&to=" + string.Join(",", Codes.Where(c => c != "EUR")));
            using var json = await JsonDocument.ParseAsync(stream);
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 1 };
            foreach (var rate in json.RootElement.GetProperty("rates").EnumerateObject()) rates[rate.Name] = rate.Value.GetDouble();
            _rates = rates;
            _rateDate = json.RootElement.TryGetProperty("date", out var date) ? date.GetString() : null;
            _fetchedAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Write("Döviz kurları alınamadı: " + ex.Message);
        }
        finally
        {
            _loading = false;
        }
        Render();
    }

    /// <summary>How many <paramref name="to"/> one <paramref name="from"/> is worth; null until the rates are in.</summary>
    private double? Rate(string from, string to) =>
        _rates.TryGetValue(from, out var a) && _rates.TryGetValue(to, out var b) && a > 0 ? b / a : null;

    private static string Symbol(string code) => code switch
    {
        "TRY" => "₺", "USD" => "$", "EUR" => "€", "GBP" => "£", "JPY" => "¥", _ => code
    };

    private static string Money(double value, string code) =>
        value.ToString(value >= 1000 ? "N0" : value >= 10 ? "N2" : "N4", CultureInfo.CurrentCulture) + " " + Symbol(code);

    private void Render()
    {
        var from = Current.CurrencyFrom;
        var to = Current.CurrencyTo;
        _pair.Text = $"1 {from}";
        _rate.Text = Rate(from, to) is { } rate ? Money(rate, to) : _loading || _fetchedAt == default ? "…" : "—";
        _tile.ToolTip = UiText.Get("CurrencyTileHint");
        if (_popup.IsOpen) RenderCard();
    }

    private void RenderCard()
    {
        var from = Current.CurrencyFrom;
        var to = Current.CurrencyTo;
        ((TextBlock)_fromButton.Content).Text = from;
        ((TextBlock)_toButton.Content).Text = to;
        var text = _amount.Text.Trim().Replace(" ", "");
        var parsed = double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount)
            || double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
        _result.Text = !parsed ? "—" : Rate(from, to) is { } rate ? Money(amount * rate, to) : "…";
        _status.Text = _rates.Count == 0
            ? UiText.Get(_loading ? "CurrencyLoading" : "CurrencyOffline")
            : UiText.Format("CurrencySource", _rateDate ?? "");
    }

    private void Cycle(bool from)
    {
        var current = from ? Current.CurrencyFrom : Current.CurrencyTo;
        var other = from ? Current.CurrencyTo : Current.CurrencyFrom;
        var next = Codes[(Array.IndexOf(Codes, current) + 1) % Codes.Length];
        if (next == other) next = Codes[(Array.IndexOf(Codes, next) + 1) % Codes.Length];
        UpdateSettings(s => from ? s with { CurrencyFrom = next } : s with { CurrencyTo = next });
    }

    private StackPanel BuildCard()
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header(UiText.Get("CurrencyTitle"), () => _popup.Close()));
        root.Children.Add(_amount);
        var pairs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        pairs.Children.Add(_fromButton);
        var swap = Ui.IconButton(Ui.Icon("IconSwap", 13), (_, _) =>
            UpdateSettings(s => s with { CurrencyFrom = s.CurrencyTo, CurrencyTo = s.CurrencyFrom }), UiText.Get("CurrencySwap"), 34, 28);
        swap.Margin = new Thickness(4, 0, 4, 0);
        pairs.Children.Add(swap);
        pairs.Children.Add(_toButton);
        root.Children.Add(pairs);
        root.Children.Add(_result);
        var hint = Ui.Text(UiText.Get("CurrencyCycleHint"), 10.5, brushKey: "TextSecondary");
        hint.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(hint);
        _status.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_status);
        return root;
    }

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.Close();
            return;
        }
        _card ??= BuildCard();
        _popup.SetContent(_card, 280);
        _popup.Toggle();
        RenderCard();
        _popup.FocusInput(_amount);
        _ = LoadAsync();
    }
}
