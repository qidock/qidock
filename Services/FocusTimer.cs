using System.Media;
using System.Windows.Threading;
using QiDock.Widgets;

namespace QiDock.Services;

/// <summary>
/// The one focus (pomodoro) timer behind the bar's focus tile and the desktop focus widget, so both always show the
/// same countdown. A focus session is followed by a short break (a long one after every fourth round); the
/// one-second timer runs only while a countdown is running.
/// </summary>
public static class FocusTimer
{
    public const int Rounds = 4;
    private static readonly TimeSpan ShortBreak = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongBreak = TimeSpan.FromMinutes(15);
    private static DispatcherTimer? _timer;
    private static TimeSpan _remaining;
    private static TimeSpan _breakLength = ShortBreak;
    private static bool _fresh = true; // a full, untouched focus session: follows the length setting
    private static DateTime _endsAt;
    private static int _completed;
    private static DateTime _completedOn = DateTime.Today;

    /// <summary>The focus length from the settings, in minutes.</summary>
    public static Func<int> Minutes { get; set; } = () => 25;

    /// <summary>Raised on every change and every second while running.</summary>
    public static event EventHandler? Changed;

    public static bool Running { get; private set; }
    public static bool OnBreak { get; private set; }
    public static int Round { get; private set; } = 1;

    public static TimeSpan FocusLength => TimeSpan.FromMinutes(Math.Clamp(Minutes(), 1, 180));

    /// <summary>Length of the current phase (focus or break), for progress rings.</summary>
    public static TimeSpan PhaseLength => OnBreak ? _breakLength : FocusLength;

    public static TimeSpan Remaining
    {
        get
        {
            var left = Running ? _endsAt - DateTime.UtcNow : _fresh && !OnBreak ? FocusLength : _remaining;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>True when there is something to reset: running, paused part-way, or on a break.</summary>
    public static bool Started => Running || OnBreak || !_fresh;

    public static int CompletedToday
    {
        get
        {
            if (_completedOn != DateTime.Today)
            {
                _completedOn = DateTime.Today;
                _completed = 0;
            }
            return _completed;
        }
    }

    public static void Toggle()
    {
        if (Running) Pause();
        else Start();
    }

    public static void Start()
    {
        if (Running) return;
        var left = Remaining;
        if (left <= TimeSpan.Zero) left = PhaseLength;
        _fresh = false;
        _endsAt = DateTime.UtcNow + left;
        Running = true;
        Timer.Start();
        Raise();
    }

    public static void Pause()
    {
        if (!Running) return;
        _remaining = Remaining;
        Running = false;
        Timer.Stop();
        Raise();
    }

    /// <summary>Back to a full focus session, stopped.</summary>
    public static void Reset()
    {
        Stop();
        OnBreak = false;
        _fresh = true;
        Raise();
    }

    /// <summary>Starts a five-minute break now.</summary>
    public static void StartBreak()
    {
        Stop();
        OnBreak = true;
        _breakLength = ShortBreak;
        _remaining = ShortBreak;
        Start();
    }

    /// <summary>Moves on to the next phase without finishing this one (a skipped session is not counted).</summary>
    public static void Skip()
    {
        Stop();
        Advance();
        Raise();
    }

    private static void Stop()
    {
        Running = false;
        _timer?.Stop();
    }

    private static void Advance()
    {
        if (OnBreak)
        {
            OnBreak = false;
            if (Round > Rounds) Round = 1;
            _fresh = true;
        }
        else
        {
            Round++;
            OnBreak = true;
            _breakLength = Round > Rounds ? LongBreak : ShortBreak;
            _remaining = _breakLength;
            _fresh = false;
        }
    }

    private static DispatcherTimer Timer
    {
        get
        {
            if (_timer is not null) return _timer;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                if (Remaining > TimeSpan.Zero)
                {
                    Raise();
                    return;
                }
                Finish();
            };
            return _timer;
        }
    }

    private static void Finish()
    {
        Stop();
        var wasBreak = OnBreak;
        if (!wasBreak)
        {
            _ = CompletedToday; // starts a new day's count after midnight
            _completed++;
        }
        Advance();
        SystemSounds.Asterisk.Play();
        Ui.ShowHud("IconTimer", UiText.Get(wasBreak ? "FocusBreakDone" : "FocusDone"));
        Raise();
    }

    private static void Raise() => Changed?.Invoke(null, EventArgs.Empty);
}
