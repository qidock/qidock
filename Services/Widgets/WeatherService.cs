using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace QiDock.Services.Widgets;

public enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

public sealed record WeatherHour(DateTime Time, double Temperature, int Code, bool IsDay);
public sealed record WeatherDay(DateTime Date, int Code, double Min, double Max);

public sealed record WeatherReport(
    string City,
    double Temperature,
    int Code,
    bool IsDay,
    double TodayMin,
    double TodayMax,
    IReadOnlyList<WeatherHour> Hours,
    IReadOnlyList<WeatherDay> Days,
    DateTime FetchedAt,
    WeatherExtra? Extra = null);

/// <summary>Wind, humidity, rain and the sun's times (used by the desktop widgets).</summary>
public sealed record WeatherExtra(double WindSpeed, double WindDirection, double WindGusts, double Humidity, double Precipitation,
    double FeelsLike, DateTime? Sunrise, DateTime? Sunset, DateTime? NextSunrise);

/// <summary>Current conditions and a 7-day forecast from Open-Meteo (free, no API key).</summary>
public sealed class WeatherService
{
    private (string Query, double Lat, double Lon, string Name)? _place;

    public async Task<WeatherReport> FetchAsync(string city, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(city)) throw new InvalidOperationException("Şehir belirtilmedi.");
        var place = _place is { } cached && string.Equals(cached.Query, city, StringComparison.OrdinalIgnoreCase)
            ? cached
            : await GeocodeAsync(city.Trim(), token).ConfigureAwait(false);
        _place = place;

        var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={place.Lat:0.####}&longitude={place.Lon:0.####}") +
            "&current=temperature_2m,weather_code,is_day,wind_speed_10m,wind_direction_10m,wind_gusts_10m,relative_humidity_2m,precipitation,apparent_temperature" +
            "&hourly=temperature_2m,weather_code,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset" +
            "&timezone=auto&forecast_days=7";
        using var response = await WidgetHttp.Client.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var root = json.RootElement;

        var current = root.GetProperty("current");
        var nowLocal = ParseTime(current.GetProperty("time").GetString());

        var hourly = root.GetProperty("hourly");
        var hourTimes = hourly.GetProperty("time").EnumerateArray().Select(t => ParseTime(t.GetString())).ToList();
        var hourTemps = hourly.GetProperty("temperature_2m").EnumerateArray().Select(ReadDouble).ToList();
        var hourCodes = hourly.GetProperty("weather_code").EnumerateArray().Select(ReadInt).ToList();
        var hourDay = hourly.GetProperty("is_day").EnumerateArray().Select(v => ReadInt(v) == 1).ToList();
        var hours = new List<WeatherHour>();
        for (var i = 0; i < hourTimes.Count && hours.Count < 7; i++)
        {
            if (hourTimes[i] < nowLocal.AddMinutes(-59)) continue;
            hours.Add(new WeatherHour(hourTimes[i], hourTemps[i], hourCodes[i], hourDay[i]));
        }

        var daily = root.GetProperty("daily");
        var dayTimes = daily.GetProperty("time").EnumerateArray().Select(t => ParseTime(t.GetString())).ToList();
        var dayCodes = daily.GetProperty("weather_code").EnumerateArray().Select(ReadInt).ToList();
        var dayMax = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(ReadDouble).ToList();
        var dayMin = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(ReadDouble).ToList();
        var days = dayTimes.Select((t, i) => new WeatherDay(t.Date, dayCodes[i], dayMin[i], dayMax[i])).ToList();
        var sunrises = daily.TryGetProperty("sunrise", out var rise) ? rise.EnumerateArray().Select(t => (DateTime?)ParseTime(t.GetString())).ToList() : [];
        var sunsets = daily.TryGetProperty("sunset", out var set) ? set.EnumerateArray().Select(t => (DateTime?)ParseTime(t.GetString())).ToList() : [];
        var extra = new WeatherExtra(
            ReadOptional(current, "wind_speed_10m"),
            ReadOptional(current, "wind_direction_10m"),
            ReadOptional(current, "wind_gusts_10m"),
            ReadOptional(current, "relative_humidity_2m"),
            ReadOptional(current, "precipitation"),
            ReadOptional(current, "apparent_temperature"),
            sunrises.Count > 0 ? sunrises[0] : null,
            sunsets.Count > 0 ? sunsets[0] : null,
            sunrises.Count > 1 ? sunrises[1] : null);

        return new WeatherReport(
            place.Name,
            ReadDouble(current.GetProperty("temperature_2m")),
            ReadInt(current.GetProperty("weather_code")),
            ReadInt(current.GetProperty("is_day")) == 1,
            days.Count > 0 ? days[0].Min : double.NaN,
            days.Count > 0 ? days[0].Max : double.NaN,
            hours,
            days,
            DateTime.Now,
            extra);
    }

    private static async Task<(string Query, double Lat, double Lon, string Name)> GeocodeAsync(string city, CancellationToken token)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&language=tr&name="
            + Uri.EscapeDataString(city);
        using var response = await WidgetHttp.Client.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            throw new InvalidOperationException("Şehir bulunamadı.");
        var first = results[0];
        return (city, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble(),
            first.TryGetProperty("name", out var name) ? name.GetString() ?? city : city);
    }

    private static DateTime ParseTime(string? value) =>
        DateTime.Parse(value ?? throw new FormatException("Zaman alanı boş."), CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static double ReadOptional(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? ReadDouble(value) : double.NaN;

    private static double ReadDouble(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
    private static int ReadInt(JsonElement value) => value.ValueKind == JsonValueKind.Number ? (int)value.GetDouble() : 0;

    public static WeatherKind Classify(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherKind.Drizzle,
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => WeatherKind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherKind.Snow,
        95 or 96 or 99 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy
    };

    public static string Describe(int code) => UiText.Get(code switch
    {
        0 => "WxClear",
        1 => "WxMostlyClear",
        2 => "WxPartlyCloudy",
        3 => "WxOvercast",
        45 or 48 => "WxFog",
        51 or 53 or 55 => "WxDrizzle",
        56 or 57 => "WxFreezingDrizzle",
        61 => "WxLightRain",
        63 => "WxRain",
        65 => "WxHeavyRain",
        66 or 67 => "WxFreezingRain",
        71 => "WxLightSnow",
        73 => "WxSnow",
        75 => "WxHeavySnow",
        77 => "WxSnowGrains",
        80 or 81 or 82 => "WxShowers",
        85 or 86 => "WxSnowShowers",
        95 => "WxThunder",
        96 or 99 => "WxHail",
        _ => "WxUnknown"
    });
}
