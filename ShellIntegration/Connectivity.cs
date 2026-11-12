using Windows.Devices.Radios;
using DeviceRadioState = Windows.Devices.Radios.RadioState;
using Windows.Networking.Connectivity;

namespace DesktopOrbit.ShellIntegration;

/// <summary>
/// Wi-Fi and Bluetooth on/off (the same switches as Windows quick settings) and the current network's name and signal.
/// </summary>
public static class Connectivity
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<RadioKind, (bool Present, bool On)> _state = new();

    /// <summary>Raised (on a background thread) when a switch was read or changed.</summary>
    public static event Action? Changed;

    /// <summary>Short enough for a bubble: "off", or the signal bars when on (the network name goes in the hint).</summary>
    public static string WifiStatus => Status(RadioKind.WiFi, SignalBars());

    private static string? SignalBars()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile is { IsWlanConnectionProfile: true } && profile.GetSignalBars() is { } bars
                ? new string('▮', bars) + new string('▯', Math.Max(0, 5 - bars))
                : "on";
        }
        catch (Exception)
        {
            return "on";
        }
    }

    public static string BluetoothStatus => Status(RadioKind.Bluetooth, null);

    public static bool? IsOn(RadioKind kind) => _state.TryGetValue(kind, out var s) && s.Present ? s.On : null;

    private static string Status(RadioKind kind, string? detail) =>
        !_state.TryGetValue(kind, out var s) ? "…"
        : !s.Present ? "not found"
        : !s.On ? "off"
        : detail ?? "on";

    /// <summary>Reads both switches in the background.</summary>
    public static void Refresh() => _ = Task.Run(RefreshAsync);

    public static async Task RefreshAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var state = new Dictionary<RadioKind, (bool, bool)>();
            foreach (var kind in new[] { RadioKind.WiFi, RadioKind.Bluetooth })
            {
                var radio = radios.FirstOrDefault(r => r.Kind == kind);
                state[kind] = (radio is not null, radio?.State == DeviceRadioState.On);
            }

            _state = state;
        }
        catch (Exception)
        {
            _state = new Dictionary<RadioKind, (bool, bool)> { [RadioKind.WiFi] = (false, false), [RadioKind.Bluetooth] = (false, false) };
        }
        finally
        {
            Gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Turns a radio on or off. Returns a message for the ring.</summary>
    public static async Task<string> ToggleAsync(RadioKind kind)
    {
        var name = kind == RadioKind.WiFi ? "Wi-Fi" : "Bluetooth";
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                return $"Windows didn't allow changing {name}. Check Settings › Privacy › Radios";
            }

            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
            if (radio is null)
            {
                return $"No {name} on this PC";
            }

            var target = radio.State == DeviceRadioState.On ? DeviceRadioState.Off : DeviceRadioState.On;
            var result = await radio.SetStateAsync(target);
            await RefreshAsync();
            return result == RadioAccessStatus.Allowed
                ? $"{name} {(target == DeviceRadioState.On ? "on" : "off")}"
                : $"Couldn't switch {name} ({result})";
        }
        catch (Exception ex)
        {
            return $"Couldn't switch {name}: {ex.Message}";
        }
    }

    /// <summary>Name of the network you're on and its signal, e.g. "HomeWifi ▂▄▆".</summary>
    public static string? NetworkName()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return "not connected";
            }

            var name = profile.IsWlanConnectionProfile
                ? profile.WlanConnectionProfileDetails.GetConnectedSsid()
                : profile.ProfileName;
            var bars = profile.GetSignalBars();
            return bars is { } b && profile.IsWlanConnectionProfile ? $"{name} {new string('▮', b)}{new string('▯', Math.Max(0, 5 - b))}" : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsWired()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile is not null && !profile.IsWlanConnectionProfile && !profile.IsWwanConnectionProfile;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
