using System.Runtime.InteropServices;

namespace DesktopOrbit.ShellIntegration;

/// <summary>
/// Screen brightness for desktop monitors through DDC/CI (the monitor's own control channel),
/// applied to every connected monitor that supports it. Talking to monitors is slow, so it runs off the UI thread.
/// </summary>
public static class BrightnessControl
{
    private static readonly object Gate = new();
    private static readonly object WriteGate = new(); // one monitor conversation at a time
    private static int? _level;
    private static bool _unsupported;
    private static Task? _work;

    public static event Action? Changed;

    public static double? Level => _level / 100.0;

    public static string Status =>_unsupported ? "n/a" : _level is { } level ? $"{level}%" : "…";

    /// <summary>Reads the current level once, in the background.</summary>
    public static void Warm()
    {
        lock (Gate)
        {
            if (_level is null && !_unsupported && _work is null)
            {
                _work = Task.Run(() =>
                {
                    var level = ForEachMonitor(handle =>
                        GetMonitorBrightness(handle, out var min, out var current, out var max) && max > min
                            ? (int)Math.Round(100.0 * (current - min) / (max - min))
                            : (int?)null);
                    lock (Gate)
                    {
                        _level = level;
                        _unsupported = level is null;
                        _work = null;
                    }

                    Changed?.Invoke();
                });
            }
        }
    }

    /// <summary>Asks the monitor directly (slow; for checking that a change really happened).</summary>
    public static int? ReadNow()
    {
        // Monitors often ignore a request that comes right after a change, so try a few times.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var level = ForEachMonitor(handle =>
                GetMonitorBrightness(handle, out var min, out var current, out var max) && max > min
                    ? (int)Math.Round(100.0 * (current - min) / (max - min))
                    : (int?)null);
            if (level is not null)
            {
                return level;
            }

            Thread.Sleep(200);
        }

        return null;
    }

    public static void Change(int percentDelta)
    {
        int target;
        lock (Gate)
        {
            if (_unsupported || _level is null)
            {
                Warm();
                return;
            }

            target = Math.Clamp(_level.Value + percentDelta, 0, 100);
            _level = target;
        }

        Changed?.Invoke();
        Task.Run(() =>
        {
            lock (Gate)
            {
                // Only the newest level matters when the wheel spins quickly.
                if (_level != target)
                {
                    return;
                }
            }

            lock (WriteGate)
            {
                ForEachMonitor(handle =>
                {
                    // Retry the read: a monitor that was just changed may not answer straight away.
                    for (var attempt = 0; attempt < 5; attempt++)
                    {
                        if (GetMonitorBrightness(handle, out var min, out _, out var max) && max > min)
                        {
                            SetMonitorBrightness(handle, (uint)Math.Round(min + (max - min) * target / 100.0));
                            break;
                        }

                        Thread.Sleep(150);
                    }

                    return (int?)null;
                });
            }
        });
    }

    /// <summary>Runs the action on each physical monitor; returns the first non-null result.</summary>
    private static int? ForEachMonitor(Func<IntPtr, int?> action)
    {
        int? result = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
            {
                return true;
            }

            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
            {
                return true;
            }

            try
            {
                foreach (var item in physical)
                {
                    result ??= action(item.Handle);
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
}

/// <summary>Reads text aloud with the Windows voices (SAPI), e.g. an article copied from Facebook or a web page.</summary>
public static class ReadAloud
{
    private static dynamic? _voice;

    public static bool IsSpeaking
    {
        get
        {
            try
            {
                return _voice is not null && (int)_voice.Status.RunningState == 2;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    public static IEnumerable<string> Voices()
    {
        var names = new List<string>();
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is null)
            {
                return names;
            }

            dynamic voice = Activator.CreateInstance(type)!;
            foreach (dynamic token in voice.GetVoices())
            {
                names.Add((string)token.GetDescription());
            }
        }
        catch (COMException)
        {
        }

        return names;
    }

    /// <summary>Starts reading, or stops if already reading. Returns a message for the ring.</summary>
    public static string Toggle(string? text)
    {
        try
        {
            if (_voice is null)
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type is null)
                {
                    return "No Windows voice is installed";
                }

                _voice = Activator.CreateInstance(type);
            }

            if (IsSpeaking)
            {
                _voice!.Speak(string.Empty, 2 /* purge */);
                return "Stopped reading";
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return "Copy some text first (select it, Ctrl+C), then click Read";
            }

            _voice!.Speak(text, 1 /* async */);
            return $"Reading {text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} words · click again to stop";
        }
        catch (COMException ex)
        {
            return $"Couldn't read aloud: {ex.Message}";
        }
    }
}
