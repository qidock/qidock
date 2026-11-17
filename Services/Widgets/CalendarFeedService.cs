using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace QiDock.Services.Widgets;

public sealed record CalendarOccurrence(string Title, string? Location, DateTime Start, DateTime End, bool AllDay);

/// <summary>
/// Reads an iCalendar (.ics) feed, such as Google Calendar's "secret address in iCal format",
/// and expands the common recurrence rules into concrete occurrences.
/// </summary>
public sealed class CalendarFeedService
{
    private const int MaxOccurrencesPerEvent = 800;
    private List<IcsEvent> _events = [];

    public bool HasFeed { get; private set; }
    public DateTime? LastUpdated { get; private set; }
    public string? LastError { get; private set; }

    public async Task RefreshAsync(string? feedUrl, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            HasFeed = false;
            _events = [];
            LastError = null;
            return;
        }

        HasFeed = true;
        try
        {
            var url = feedUrl.Trim();
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));
            using var response = await WidgetHttp.Client.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            _events = Parse(text);
            LastUpdated = DateTime.Now;
            LastError = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or UriFormatException or InvalidOperationException)
        {
            LastError = UiText.Get("CalendarFeedError");
        }
    }

    public IReadOnlyList<CalendarOccurrence> GetOccurrences(DateTime from, DateTime to)
    {
        var result = new List<CalendarOccurrence>();
        var overrides = _events.Where(e => e.RecurrenceId is not null)
            .Select(e => (e.Uid, e.RecurrenceId!.Value))
            .ToHashSet();

        foreach (var item in _events)
        {
            foreach (var start in Expand(item, from, to))
            {
                if (item.RecurrenceId is null && item.Rule is not null && overrides.Contains((item.Uid, start))) continue;
                var end = start + item.Duration;
                if (end <= from || start >= to) continue;
                result.Add(new CalendarOccurrence(item.Summary, item.Location, start, end, item.AllDay));
            }
        }

        result.Sort((a, b) => a.AllDay != b.AllDay ? (a.AllDay ? -1 : 1) : a.Start.CompareTo(b.Start));
        return result;
    }

    private static IEnumerable<DateTime> Expand(IcsEvent item, DateTime from, DateTime to)
    {
        if (item.Rule is not { } rule)
        {
            yield return item.Start;
            yield break;
        }

        var produced = 0;
        var yielded = 0;
        var interval = Math.Max(1, rule.Interval);
        var limit = rule.Until ?? DateTime.MaxValue;
        var windowEnd = to < limit ? to : limit;

        IEnumerable<DateTime> Candidates()
        {
            var anchor = item.Start;
            for (var period = 0; period < 20000; period++)
            {
                switch (rule.Frequency)
                {
                    case "DAILY":
                        yield return anchor.AddDays(period * interval);
                        break;
                    case "WEEKLY":
                    {
                        var weekStart = anchor.Date.AddDays(-(((int)anchor.DayOfWeek + 6) % 7)).AddDays(period * 7 * interval);
                        var days = rule.ByDay.Count > 0 ? rule.ByDay.Select(d => d.Day) : [anchor.DayOfWeek];
                        foreach (var day in days.Distinct().OrderBy(d => ((int)d + 6) % 7))
                            yield return weekStart.AddDays(((int)day + 6) % 7) + anchor.TimeOfDay;
                        break;
                    }
                    case "MONTHLY":
                    {
                        var month = new DateTime(anchor.Year, anchor.Month, 1).AddMonths(period * interval);
                        if (rule.ByDay.Count > 0)
                        {
                            foreach (var (ordinal, day) in rule.ByDay)
                                if (NthWeekday(month, day, ordinal == 0 ? 1 : ordinal) is { } date)
                                    yield return date + anchor.TimeOfDay;
                        }
                        else
                        {
                            var dayOfMonth = rule.ByMonthDay ?? anchor.Day;
                            if (dayOfMonth <= DateTime.DaysInMonth(month.Year, month.Month))
                                yield return month.AddDays(dayOfMonth - 1) + anchor.TimeOfDay;
                        }
                        break;
                    }
                    case "YEARLY":
                    {
                        var year = anchor.Year + period * interval;
                        if (anchor.Month == 2 && anchor.Day == 29 && !DateTime.IsLeapYear(year)) break;
                        yield return new DateTime(year, anchor.Month, anchor.Day) + anchor.TimeOfDay;
                        break;
                    }
                    default:
                        yield return anchor;
                        yield break;
                }
            }
        }

        foreach (var candidate in Candidates())
        {
            if (candidate < item.Start) continue;
            if (candidate > windowEnd) yield break;
            if (rule.Count is { } count && produced >= count) yield break;
            produced++;
            if (item.Exclusions.Contains(candidate)) continue;
            if (candidate + item.Duration > from && ++yielded <= MaxOccurrencesPerEvent) yield return candidate;
        }
    }

    private static DateTime? NthWeekday(DateTime month, DayOfWeek day, int ordinal)
    {
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        var matches = Enumerable.Range(0, days).Select(i => month.AddDays(i)).Where(d => d.DayOfWeek == day).ToList();
        var index = ordinal > 0 ? ordinal - 1 : matches.Count + ordinal;
        return index >= 0 && index < matches.Count ? matches[index] : null;
    }

    // ---- Parsing -------------------------------------------------------------------------

    private sealed record RecurrenceRule(string Frequency, int Interval, int? Count, DateTime? Until,
        IReadOnlyList<(int Ordinal, DayOfWeek Day)> ByDay, int? ByMonthDay);

    private sealed class IcsEvent
    {
        public string Uid = "";
        public string Summary = "";
        public string? Location;
        public DateTime Start;
        public TimeSpan Duration;
        public bool AllDay;
        public RecurrenceRule? Rule;
        public DateTime? RecurrenceId;
        public HashSet<DateTime> Exclusions = [];
    }

    private static List<IcsEvent> Parse(string text)
    {
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("iCalendar verisi değil.");

        var lines = Unfold(text);
        var events = new List<IcsEvent>();
        IcsEvent? current = null;
        DateTime? end = null;
        TimeSpan? duration = null;
        var cancelled = false;
        var depth = 0;

        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new IcsEvent();
                end = null;
                duration = null;
                cancelled = false;
                depth = 0;
                continue;
            }

            if (current is null) continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase) && depth > 0) { depth--; continue; }
            if (depth > 0) continue; // VALARM and other nested components

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (!cancelled && current.Start != default)
                {
                    current.Duration = duration ?? (end is { } e ? e - current.Start
                        : current.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
                    if (current.Duration < TimeSpan.Zero) current.Duration = TimeSpan.Zero;
                    events.Add(current);
                }
                current = null;
                continue;
            }

            var colon = FindValueSeparator(line);
            if (colon < 0) continue;
            var head = line[..colon];
            var value = line[(colon + 1)..];
            var parts = head.Split(';');
            var name = parts[0].ToUpperInvariant();
            var parameters = parts.Skip(1)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].Trim('"'), StringComparer.OrdinalIgnoreCase);

            switch (name)
            {
                case "UID": current.Uid = value; break;
                case "SUMMARY": current.Summary = UnescapeText(value); break;
                case "LOCATION": current.Location = string.IsNullOrWhiteSpace(value) ? null : UnescapeText(value); break;
                case "STATUS": cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "DTSTART":
                    (current.Start, current.AllDay) = ParseDate(value, parameters);
                    break;
                case "DTEND": end = ParseDate(value, parameters).Value; break;
                case "DURATION": duration = ParseDuration(value); break;
                case "RRULE": current.Rule = ParseRule(value); break;
                case "RECURRENCE-ID": current.RecurrenceId = ParseDate(value, parameters).Value; break;
                case "EXDATE":
                    foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        current.Exclusions.Add(ParseDate(item, parameters).Value);
                    break;
            }
        }

        foreach (var item in events.Where(e => string.IsNullOrEmpty(e.Summary)))
            item.Summary = UiText.Get("CalendarUntitled");
        return events;
    }

    private static int FindValueSeparator(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }

    private static List<string> Unfold(string text)
    {
        var result = new List<string>();
        var builder = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                builder.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (builder.Length > 0) result.Add(builder.ToString());
            builder.Clear().Append(raw.TrimEnd('\r'));
        }
        if (builder.Length > 0) result.Add(builder.ToString());
        return result;
    }

    private static string UnescapeText(string value) => value
        .Replace("\\n", " ", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private static (DateTime Value, bool AllDay) ParseDate(string value, IReadOnlyDictionary<string, string> parameters)
    {
        value = value.Trim();
        if (value.Length == 8 || (parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase)))
            return (DateTime.ParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture), true);

        var utc = value.EndsWith('Z');
        var parsed = DateTime.ParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (utc) return (DateTime.SpecifyKind(parsed, DateTimeKind.Utc).ToLocalTime(), false);

        if (parameters.TryGetValue("TZID", out var zoneId) && FindZone(zoneId) is { } zone)
        {
            try
            {
                var utcTime = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), zone);
                return (utcTime.ToLocalTime(), false);
            }
            catch (ArgumentException)
            {
                // Invalid local time in that zone (DST gap); fall through and keep it as written.
            }
        }

        return (DateTime.SpecifyKind(parsed, DateTimeKind.Local), false);
    }

    private static readonly Dictionary<string, TimeZoneInfo?> ZoneCache = new(StringComparer.OrdinalIgnoreCase);

    private static TimeZoneInfo? FindZone(string id)
    {
        lock (ZoneCache)
        {
            if (ZoneCache.TryGetValue(id, out var cached)) return cached;
            TimeZoneInfo? zone = null;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
            ZoneCache[id] = zone;
            return zone;
        }
    }

    private static TimeSpan ParseDuration(string value)
    {
        // RFC 5545 duration: [+-]P[nW][nD][T[nH][nM][nS]]
        var negative = value.StartsWith('-');
        var text = value.TrimStart('+', '-');
        if (!text.StartsWith('P')) return TimeSpan.Zero;
        var total = TimeSpan.Zero;
        var number = 0;
        foreach (var c in text[1..])
        {
            if (char.IsDigit(c)) { number = number * 10 + (c - '0'); continue; }
            total += c switch
            {
                'W' => TimeSpan.FromDays(7 * number),
                'D' => TimeSpan.FromDays(number),
                'H' => TimeSpan.FromHours(number),
                'M' => TimeSpan.FromMinutes(number),
                'S' => TimeSpan.FromSeconds(number),
                _ => TimeSpan.Zero
            };
            number = 0;
        }
        return negative ? -total : total;
    }

    private static RecurrenceRule? ParseRule(string value)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        if (!parts.TryGetValue("FREQ", out var frequency)) return null;
        frequency = frequency.ToUpperInvariant();
        if (frequency is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY")) return null;

        var interval = parts.TryGetValue("INTERVAL", out var i) && int.TryParse(i, out var parsedInterval) ? parsedInterval : 1;
        int? count = parts.TryGetValue("COUNT", out var c) && int.TryParse(c, out var parsedCount) ? parsedCount : null;
        DateTime? until = null;
        if (parts.TryGetValue("UNTIL", out var u))
        {
            try { until = ParseDate(u, new Dictionary<string, string>()).Value; }
            catch (FormatException) { }
            if (until is { } untilValue && untilValue.TimeOfDay == TimeSpan.Zero && u.Trim().Length == 8)
                until = untilValue.AddDays(1).AddTicks(-1);
        }

        var byDay = new List<(int, DayOfWeek)>();
        if (parts.TryGetValue("BYDAY", out var days))
        {
            foreach (var token in days.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var code = token.Length >= 2 ? token[^2..].ToUpperInvariant() : "";
                DayOfWeek? day = code switch
                {
                    "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
                    "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
                    "SU" => DayOfWeek.Sunday, _ => null
                };
                if (day is null) continue;
                var ordinalText = token[..^2];
                var ordinal = int.TryParse(ordinalText, out var n) ? n : 0;
                byDay.Add((ordinal, day.Value));
            }
        }

        int? byMonthDay = parts.TryGetValue("BYMONTHDAY", out var md) && int.TryParse(md.Split(',')[0], out var parsedDay) && parsedDay > 0
            ? parsedDay : null;
        return new RecurrenceRule(frequency, interval, count, until, byDay, byMonthDay);
    }
}
