using System.IO;
using System.Text.Json;

namespace DesktopOrbit.ShellIntegration;

/// <summary>Settings for the v2 ring, stored next to the classic settings in %LOCALAPPDATA%\DesktopOrbit\ring.json.</summary>
public sealed class RingSettings
{
    public const int MaxFavorites = 14; // Apps shows 7 + "More" when there are more than 8

    public static string SettingsPath => FilePath;

    /// <summary>Reads a setup file someone saved; null if it isn't one.</summary>
    public static RingSettings? TryLoadFrom(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<RingSettings>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "ring.json");

    /// <summary>App group keys (lower-case names) pinned to the Apps section. Null until first set up.</summary>
    public List<string>? FavoriteApps { get; set; }

    public Dictionary<string, int> LaunchCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null until the first run, when the desktop right-click entry is turned on.</summary>
    public bool? DesktopMenuEnabled { get; set; }

    public bool StartWithWindows { get; set; }

    public int LastSector { get; set; }

    public List<Station> RadioFavorites { get; set; } = [];

    public List<Station> RadioRecent { get; set; } = [];

    public int RadioVolume { get; set; } = 60;

    /// <summary>Countries offered in the Radio section (two-letter codes); "World" is always added.</summary>
    public List<string> RadioCountries { get; set; } = [System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName];

    /// <summary>When pinned, the ring stays open at the place it was dragged to.</summary>
    public bool Pinned { get; set; }

    public double? PinnedLeft { get; set; }

    public double? PinnedTop { get; set; }

    public bool PinnedMirrored { get; set; }

    /// <summary>Watch face in the centre of the ring: 0 digital, 1 classic, 2 glow, 3 calendar, 4 radio.</summary>
    public int ClockFace { get; set; }

    /// <summary>Watch colour: 0 glass, 1 purple, 2 ocean, 3 lime, 4 sunset.</summary>
    public int ClockColour { get; set; }

    /// <summary>"Alt+Space" or "Ctrl+Alt+Space".</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>The look of the whole ring (see Theme.BuiltIn, or your own in the themes folder).</summary>
    public string ThemeName { get; set; } = "Glass";

    /// <summary>Browser profiles shown straight in Social (others are under Browsers). Default: your two Chrome accounts.</summary>
    public List<string> PinnedBrowserProfiles { get; set; } = ["Chrome:Default", "Edge:Default"];

    /// <summary>Places on the World watch (time and weather). Default Malta and the UK; anyone can change them.</summary>
    public List<Place> Places { get; set; } = WorldService.LocalPlaces();

    /// <summary>Shortcuts, programs, files and folders you dragged or pasted into the ring.</summary>
    public List<CustomPin> CustomPins { get; set; } = [];

    /// <summary>Work timer length in minutes (default 2 hours).</summary>
    public int TimerMinutes { get; set; } = 120;

    /// <summary>When a running timer will ring; null when not running.</summary>
    public DateTime? TimerEndsAt { get; set; }

    /// <summary>Seconds left on a paused timer; null when not paused.</summary>
    public int? TimerPausedSeconds { get; set; }

    public static RingSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<RingSettings>(File.ReadAllText(FilePath)) ?? new RingSettings();
                settings.LaunchCounts = new Dictionary<string, int>(settings.LaunchCounts, StringComparer.OrdinalIgnoreCase);
                return settings;
            }
        }
        catch (JsonException)
        {
            // A damaged file falls back to defaults rather than stopping the launcher.
        }
        catch (IOException)
        {
        }

        return new RingSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
    }

    public void CountLaunch(string key)
    {
        LaunchCounts[key] = LaunchCounts.GetValueOrDefault(key) + 1;
        Save();
    }
}

/// <summary>Something you added to the ring yourself. Section is "apps", "files" or "web".</summary>
public sealed class CustomPin
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Section { get; set; } = "apps";

    /// <summary>The program a shortcut points to (used to spot its open windows); empty if not a program.</summary>
    public string Target { get; set; } = string.Empty;
}
