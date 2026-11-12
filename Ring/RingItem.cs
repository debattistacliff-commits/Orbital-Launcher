using System.Windows.Media;

namespace DesktopOrbit.Ring;

/// <summary>One bubble that fans out from the ring: an app, a folder, an action, or a search result.</summary>
public sealed class RingItem
{
    public required string Title { get; init; }

    /// <summary>Second line in the search results list, e.g. a file's folder.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Small badge under the name, e.g. "Edge" or "Store".</summary>
    public string? Badge { get; init; }

    /// <summary>Shell path used to fetch the real Windows icon.</summary>
    public string? IconPath { get; init; }

    /// <summary>Line-art icon (24x24 path data) used when there is no shell icon.</summary>
    public string? Glyph { get; init; }

    /// <summary>Shown in the hub while the item is highlighted.</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary>Key for counting launches, so often-used items rank higher in search.</summary>
    public string? CountKey { get; init; }

    public required Action Activate { get; init; }

    /// <summary>Right-click action. Returns a message for the hub, or null.</summary>
    public Func<string?>? Secondary { get; init; }

    /// <summary>When false the ring stays open after activation (used for hints such as "coming soon").</summary>
    public bool ClosesRing { get; init; } = true;

    /// <summary>Live status shown instead of <see cref="Badge"/>, e.g. a volume level.</summary>
    public Func<string>? Status { get; init; }

    /// <summary>A 0–1 level drawn as a ring around the bubble (volume, mic, brightness); null hides it.</summary>
    public Func<double?>? Level { get; init; }

    /// <summary>Mouse wheel / +/- keys over the bubble: +1 up, -1 down (used for volume).</summary>
    public Action<int>? Adjust { get; init; }

    /// <summary>Loads an icon from elsewhere (e.g. a station logo); the glyph shows until it arrives or if it fails.</summary>
    public Func<Task<ImageSource?>>? IconLoader { get; init; }

    /// <summary>Finds this item's open windows (apps, browser profiles): a green dot shows it's open, click switches to it, × closes it.</summary>
    public Func<IReadOnlyList<DesktopOrbit.ShellIntegration.AppWindow>, List<DesktopOrbit.ShellIntegration.AppWindow>>? OpenWindows { get; init; }

    /// <summary>Show the loaded icon as a circle (profile pictures).</summary>
    public bool RoundIcon { get; init; }

    /// <summary>Short text drawn large instead of an icon, e.g. a country code.</summary>
    public string? Monogram { get; init; }

    /// <summary>Rebuild the bubbles after activation (e.g. the radio's now-playing bubble appears).</summary>
    public bool Rebuild { get; init; }

    public ImageSource? Icon { get; set; }
}

internal static class Glyphs
{
    public const string Apps = "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h6v6h-6z";
    public const string Web = "M12 3a9 9 0 1 0 0.01 0z M3 12h18 M12 3c3.2 3 3.2 15 0 18 M12 3c-3.2 3-3.2 15 0 18";
    public const string Files = "M3 6.5h7l2 2.2h9V19H3z";
    public const string Radio = "M4 9h16v11H4z M7.5 9l9-5 M15 13a2 2 0 1 0 0.01 0z M7 13h4 M7 16.5h4";
    public const string Mic = "M12 3a3 3 0 0 1 3 3v6a3 3 0 0 1-6 0V6a3 3 0 0 1 3-3z M5.5 11.5a6.5 6.5 0 0 0 13 0 M12 18v3";
    public const string System = "M12 8.5a3.5 3.5 0 1 0 0.01 0z M12 2.5v3 M12 18.5v3 M2.5 12h3 M18.5 12h3 M5.3 5.3l2.1 2.1 M16.6 16.6l2.1 2.1 M5.3 18.7l2.1-2.1 M16.6 7.4l2.1-2.1";
    public const string Camera = "M4 8h3.5l1.8-2.5h5.4L16.5 8H20v11H4z M12 10.5a3.2 3.2 0 1 0 0.01 0z";
    public const string Snip = "M6 3v13h13 M3 6h13v13 M14 9l-4 4";
    public const string Record = "M3 6h13v12H3z M16 10l5-3v10l-5-3 M8.5 12a1.5 1.5 0 1 0 0.01 0z";
    public const string Lock = "M6 11h12v9H6z M8.5 11V8a3.5 3.5 0 0 1 7 0v3";
    public const string Gauge = "M4 19h16 M6 15l3-4 3 2 4-6 2 3";
    public const string Gear = System;
    public const string Speaker = "M4 9.5h3.5L12 6v12l-4.5-3.5H4z M15.5 9a4 4 0 0 1 0 6 M18 6.5a7.5 7.5 0 0 1 0 11";
    public const string MicSmall = "M12 4a2.5 2.5 0 0 1 2.5 2.5v5a2.5 2.5 0 0 1-5 0v-5A2.5 2.5 0 0 1 12 4z M6.5 11.5a5.5 5.5 0 0 0 11 0 M12 17v3.5";
    public const string Clipboard = "M8 4h8v3H8z M6 5.5H5v15h14v-15h-1 M8.5 11h7 M8.5 14.5h7";
    public const string Plus = "M12 5v14 M5 12h14";
    public const string Sliders = "M4 7h9 M17 7h3 M15 5v4 M4 12h3 M11 12h9 M9 10v4 M4 17h11 M19 17h1 M17 15v4";
    public const string Keyboard = "M3 7h18v10H3z M6 10h1 M9 10h1 M12 10h1 M15 10h1 M18 10h0.5 M6 13h1 M18 13h0.5 M9 14h6";
    public const string Wifi = "M2.5 9a14 14 0 0 1 19 0 M5.5 12.2a9.5 9.5 0 0 1 13 0 M8.6 15.4a5 5 0 0 1 6.8 0 M12 18.6h0.01";
    public const string Bluetooth = "M7 7l10 10-5 4V3l5 4L7 17";
    public const string Desktop = "M3 4.5h18v12H3z M8 20h8 M12 16.5V20";
    public const string Bell = "M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z M10 20.5a2 2 0 0 0 4 0";
    public const string Smile = "M12 3a9 9 0 1 0 0.01 0z M8.5 14a4 4 0 0 0 7 0 M9 9.5h0.01 M15 9.5h0.01";
    public const string Timer = "M12 7.5a6.5 6.5 0 1 0 0.01 0z M12 10v4l2.5 1.5 M9.5 3.5h5 M12 3.5v4";
    public const string Stop = "M7 7h10v10H7z";
    public const string Back = "M10 6l-6 6 6 6 M4 12h16";
    public const string Power = "M12 3.5v8 M7 6.6a7.5 7.5 0 1 0 10 0";
    public const string Restart = "M4.5 12a7.5 7.5 0 1 0 2.2-5.3 M4.5 4.5v4.5H9";
    public const string Sun = "M12 8.5a3.5 3.5 0 1 0 0.01 0z M12 2.5v2 M12 19.5v2 M2.5 12h2 M19.5 12h2 M5.3 5.3l1.4 1.4 M17.3 17.3l1.4 1.4 M5.3 18.7l1.4-1.4 M17.3 6.7l1.4-1.4";
    public const string Read = "M3 5.5h6a3 3 0 0 1 3 3V19a2.2 2.2 0 0 0-2.2-2.2H3z M21 5.5h-6a3 3 0 0 0-3 3V19a2.2 2.2 0 0 1 2.2-2.2H21z";
    public const string Pin = "M9 3.5h6l-1 5.5 3.2 3.2H6.8L10 9z M12 12.2V21";
    public const string Search = "M10.5 4a6.5 6.5 0 1 0 0.01 0z M15.5 15.5L20 20";
}
