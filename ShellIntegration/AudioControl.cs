using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DesktopOrbit.ShellIntegration;

/// <summary>Volume, mute and live level for the default speakers and the default microphone (Windows Core Audio, via NAudio).</summary>
public sealed class AudioControl
{
    public static readonly AudioControl Speakers = new(DataFlow.Render, Role.Multimedia);
    public static readonly AudioControl Microphone = new(DataFlow.Capture, Role.Console);

    private readonly DataFlow _flow;
    private readonly Role _role;

    private AudioControl(DataFlow flow, Role role)
    {
        _flow = flow;
        _role = role;
    }

    /// <summary>Volume 0–100, or null when there is no such device.</summary>
    public int? Level => Use(device => (int?)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100), null);

    public bool IsMuted => Use(device => device.AudioEndpointVolume.Mute, false);

    /// <summary>How loud the sound on this device is right now (0–1), or null without a device.</summary>
    public float? Peak => Use(device => (float?)device.AudioMeterInformation.MasterPeakValue, null);

    /// <summary>Short status for the bubble, e.g. "45%" or "muted".</summary>
    public string Status => Level is { } level ? (IsMuted ? "muted" : $"{level}%") : "no device";

    public void Change(int percentDelta)
    {
        Use(device =>
        {
            var volume = device.AudioEndpointVolume;
            volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + percentDelta / 100f, 0f, 1f);
            if (percentDelta > 0 && volume.Mute)
            {
                volume.Mute = false; // turning it up means you want to hear it
            }

            return true;
        }, false);
    }

    public void ToggleMute() => Use(device => device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute, false);

    private T Use<T>(Func<MMDevice, T> action, T fallback)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(_flow, _role))
            {
                return fallback;
            }

            using var device = enumerator.GetDefaultAudioEndpoint(_flow, _role);
            return action(device);
        }
        catch (COMException)
        {
            return fallback;
        }
    }
}
