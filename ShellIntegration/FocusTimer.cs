using System.IO;
using System.Media;
using System.Windows.Threading;

namespace DesktopOrbit.ShellIntegration;

/// <summary>
/// A work timer: "I'm going to work for 2 hours". Counts down even while the ring is closed (and across restarts),
/// then rings with a repeating chime until you stop it.
/// </summary>
public sealed class FocusTimer
{
    private static readonly TimeSpan MaxRinging = TimeSpan.FromMinutes(2);

    private readonly RingSettings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private SoundPlayer? _chime;
    private DateTime _ringingSince;

    public FocusTimer(RingSettings settings)
    {
        _settings = settings;
        _tick.Tick += (_, _) => Tick();
        _tick.Start(); // a timer that ran out while the app was closed rings on the first tick
    }

    /// <summary>Raised every second while counting, and on any change.</summary>
    public event Action? Changed;

    /// <summary>Raised once when the time is up.</summary>
    public event Action? Finished;

    public TimeSpan Duration => TimeSpan.FromMinutes(Math.Max(1, _settings.TimerMinutes));

    public bool IsRunning => _settings.TimerEndsAt is not null;

    public bool IsPaused => _settings.TimerPausedSeconds is not null;

    public bool IsRinging { get; private set; }

    public DateTime? EndsAt => _settings.TimerEndsAt;

    public TimeSpan Remaining =>
        _settings.TimerEndsAt is { } ends ? Max(ends - DateTime.Now, TimeSpan.Zero)
        : _settings.TimerPausedSeconds is { } paused ? TimeSpan.FromSeconds(paused)
        : Duration;

    /// <summary>0 at the start, 1 when time is up.</summary>
    public double Progress => IsRinging ? 1 : 1 - Remaining.TotalSeconds / Math.Max(1, Duration.TotalSeconds);

    /// <summary>Self-test only: a few-seconds timer, and no ring popping up on screen when it finishes.</summary>
    internal bool QuietForTest { get; set; }

    internal void StartForTest(TimeSpan span)
    {
        StopRinging();
        _settings.TimerPausedSeconds = null;
        _settings.TimerEndsAt = DateTime.Now + span;
        Changed?.Invoke();
    }

    public void SetDuration(TimeSpan duration)
    {
        _settings.TimerMinutes = (int)Math.Clamp(Math.Round(duration.TotalMinutes), 1, 24 * 60);
        _settings.TimerPausedSeconds = null;
        _settings.Save();
        Changed?.Invoke();
    }

    /// <summary>Change the length by whole steps (scroll): 5 minutes under an hour, 15 minutes above.</summary>
    public void Nudge(int direction)
    {
        if (IsRunning)
        {
            return;
        }

        var minutes = _settings.TimerMinutes;
        var step = minutes < 60 || (minutes == 60 && direction < 0) ? 5 : 15;
        SetDuration(TimeSpan.FromMinutes(Math.Clamp(minutes + direction * step, 5, 12 * 60)));
    }

    public void Start(TimeSpan? duration = null)
    {
        StopRinging();
        if (duration is { } length)
        {
            _settings.TimerMinutes = (int)Math.Clamp(Math.Round(length.TotalMinutes), 1, 24 * 60);
            _settings.TimerPausedSeconds = null;
        }

        var remaining = _settings.TimerPausedSeconds is { } paused ? TimeSpan.FromSeconds(paused) : Duration;
        _settings.TimerPausedSeconds = null;
        _settings.TimerEndsAt = DateTime.Now + remaining;
        _settings.Save();
        Changed?.Invoke();
    }

    public void Pause()
    {
        if (_settings.TimerEndsAt is not { } ends)
        {
            return;
        }

        _settings.TimerPausedSeconds = Math.Max(1, (int)(ends - DateTime.Now).TotalSeconds);
        _settings.TimerEndsAt = null;
        _settings.Save();
        Changed?.Invoke();
    }

    public void Cancel()
    {
        StopRinging();
        _settings.TimerEndsAt = null;
        _settings.TimerPausedSeconds = null;
        _settings.Save();
        Changed?.Invoke();
    }

    /// <summary>The one button: start, pause, resume, or stop the ringing.</summary>
    public string Press()
    {
        if (IsRinging)
        {
            Cancel();
            return "Timer stopped";
        }

        if (IsRunning)
        {
            Pause();
            return $"Paused with {Format(Remaining)} left";
        }

        Start();
        return $"Timer started: {Format(Remaining)}, ends at {EndsAt:HH:mm}";
    }

    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";

    public static string Describe(TimeSpan time) =>
        time.TotalMinutes < 60 ? $"{(int)time.TotalMinutes} min"
        : time.Minutes == 0 ? $"{(int)time.TotalHours} h"
        : $"{(int)time.TotalHours} h {time.Minutes} min";

    private void Tick()
    {
        if (IsRinging)
        {
            if (DateTime.Now - _ringingSince > MaxRinging)
            {
                StopRinging(); // don't ring forever if you're away from the PC
                Changed?.Invoke();
            }

            return;
        }

        if (_settings.TimerEndsAt is not { } ends)
        {
            return;
        }

        if (DateTime.Now >= ends)
        {
            _settings.TimerEndsAt = null;
            _settings.Save();
            IsRinging = true;
            _ringingSince = DateTime.Now;
            PlayChime();
            Finished?.Invoke();
        }

        Changed?.Invoke();
    }

    private void PlayChime()
    {
        try
        {
            _chime ??= new SoundPlayer(new MemoryStream(BuildChime()));
            _chime.PlayLooping();
        }
        catch (Exception)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    private void StopRinging()
    {
        if (!IsRinging)
        {
            return;
        }

        IsRinging = false;
        _chime?.Stop();
    }

    /// <summary>A soft two-note "ding-dong" followed by a pause, as a WAV file in memory, made once.</summary>
    private static byte[] BuildChime()
    {
        const int rate = 44100;
        const double length = 2.4; // seconds per loop
        var samples = new short[(int)(rate * length)];
        void Note(double start, double frequency, double duration)
        {
            var first = (int)(start * rate);
            for (var i = 0; i < (int)(duration * rate) && first + i < samples.Length; i++)
            {
                var t = (double)i / rate;
                var envelope = Math.Exp(-t * 4.5) * Math.Min(1, t * 200); // quick attack, bell-like decay
                var value = envelope * (Math.Sin(2 * Math.PI * frequency * t) + 0.35 * Math.Sin(2 * Math.PI * frequency * 2 * t));
                samples[first + i] = (short)Math.Clamp(samples[first + i] + value * 9000, short.MinValue, short.MaxValue);
            }
        }

        Note(0.0, 880, 1.0);   // A5
        Note(0.45, 659.3, 1.2); // E5

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);      // PCM
        writer.Write((short)1);      // mono
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
