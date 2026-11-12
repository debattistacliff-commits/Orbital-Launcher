using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace DesktopOrbit.ShellIntegration;

/// <summary>
/// The look of the whole ring: highlight colours, the ink used for text and lines, the glass tint, the font and
/// how thick the outlines are. Built-in themes are below; anyone can make their own as a small .json file
/// in %LOCALAPPDATA%\DesktopOrbit\themes and share it.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "Glass";
    public string Description { get; set; } = string.Empty;

    /// <summary>Highlights: selection rings, the playing station, buttons ("#RRGGBB").</summary>
    public string Accent { get; set; } = "#F08A24";

    /// <summary>The section you're in.</summary>
    public string Active { get; set; } = "#5B8DB8";

    /// <summary>Text, icons and lines.</summary>
    public string Ink { get; set; } = "#FFFFFF";

    /// <summary>The tint over the frosted glass.</summary>
    public string Base { get; set; } = "#14171C";

    /// <summary>Soft shadow behind text (dark on dark themes, light on light ones).</summary>
    public string Shadow { get; set; } = "#000000";

    public string Font { get; set; } = "Segoe UI Variable Display, Segoe UI";

    /// <summary>How strong the tint is (1 = normal glass, 3 = nearly solid).</summary>
    public double Tint { get; set; } = 1;

    /// <summary>Outline thickness (1 = thin; Cartoon uses thick outlines).</summary>
    public double Outline { get; set; } = 1;

    /// <summary>How much of the frosted desktop shows through (0–1).</summary>
    public double Glass { get; set; } = 1;

    /// <summary>Watch colour that suits this theme: 0 glass, 1 purple, 2 ocean, 3 lime, 4 sunset.</summary>
    public int WatchColour { get; set; }

    // ---------------------------------------------------------------- current theme (set at start-up)

    private static Theme? _current;

    // Looked up on first use: the built-in list below must exist first.
    public static Theme Current => _current ??= BuiltIn[0];

    public static void Use(Theme theme)
    {
        _current = theme;
        _ink = null;
    }

    private static SolidColorBrush? _ink;

    public static Color AccentColor => Parse(Current.Accent);
    public static Color ActiveColor => Parse(Current.Active);
    public static Color ShadowColor => Parse(Current.Shadow);
    public static string FontFamily => Current.Font;

    /// <summary>Ink with transparency, e.g. Ink(0x40) for faint lines.</summary>
    public static Color InkA(byte alpha)
    {
        var c = Parse(Current.Ink);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>Glass tint with transparency, made stronger or weaker by the theme.</summary>
    public static Color BaseA(byte alpha)
    {
        var c = Parse(Current.Base);
        return Color.FromArgb((byte)Math.Clamp(alpha * Current.Tint, 0, 255), c.R, c.G, c.B);
    }

    public static SolidColorBrush InkBrush
    {
        get
        {
            if (_ink is null)
            {
                _ink = new SolidColorBrush(Parse(Current.Ink));
                _ink.Freeze();
            }

            return _ink;
        }
    }

    public static double Line(double thickness) => thickness * Current.Outline;

    public static Color Parse(string hex)
    {
        try
        {
            var text = hex.TrimStart('#');
            var value = uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return text.Length == 8
                ? Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value)
                : Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }
        catch (Exception)
        {
            return Colors.Magenta; // a broken colour shows up clearly rather than crashing
        }
    }

    // ---------------------------------------------------------------- built-in and shared themes

    public static readonly Theme[] BuiltIn =
    [
        new() { Name = "Glass", Description = "Frosted glass, orange and steel blue" },
        new() { Name = "Titanium", Description = "Dark brushed metal with cyan, like the original launcher", Accent = "#2FD4E8", Active = "#6E7B85", Ink = "#F2F5F6", Base = "#0B0D0F", Tint = 1.7, Font = "Segoe UI Semibold, Segoe UI" },
        new() { Name = "Midnight", Description = "Deep night blue, calm and dark", Accent = "#7AA2FF", Active = "#34477A", Ink = "#E8ECFF", Base = "#05070D", Tint = 1.9 },
        new() { Name = "Light frost", Description = "Bright white glass for light wallpapers", Accent = "#E0661A", Active = "#4A7FB0", Ink = "#1E2228", Base = "#FFFFFF", Shadow = "#FFFFFF", Tint = 1.6, Font = "Segoe UI" },
        new() { Name = "Neon", Description = "Purple and pink glow", Accent = "#FF4FD8", Active = "#7B35E8", Ink = "#FFFFFF", Base = "#12051F", Tint = 1.6, WatchColour = 1 },
        new() { Name = "Ocean", Description = "Sea blues and teal", Accent = "#3FD0C9", Active = "#1F6FB2", Ink = "#EAF6FF", Base = "#04182A", Tint = 1.5, WatchColour = 2 },
        new() { Name = "Sunset", Description = "Warm orange and red", Accent = "#FF6A3D", Active = "#B3334A", Ink = "#FFF4EC", Base = "#2A0D10", Tint = 1.4, WatchColour = 4 },
        new() { Name = "Forest", Description = "Greens and leaf light", Accent = "#9BD35A", Active = "#3E7B4F", Ink = "#F0F7EC", Base = "#0E1A10", Tint = 1.5, WatchColour = 3 },
        new() { Name = "Cartoon", Description = "Bright and playful: cream bubbles, bold outlines, a fun font", Accent = "#FF8A00", Active = "#FF5D8F", Ink = "#1B1B2F", Base = "#FFF3C4", Shadow = "#FFFFFF", Tint = 2.6, Outline = 2.6, Font = "Comic Sans MS, Segoe Print, Segoe UI", WatchColour = 3 },
        new() { Name = "High contrast", Description = "Solid black, bright yellow, easy to read", Accent = "#FFE600", Active = "#0078D7", Ink = "#FFFFFF", Base = "#000000", Tint = 3, Outline = 2, Glass = 0.4, Font = "Segoe UI Semibold, Segoe UI" }
    ];

    public static string ThemesFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "themes");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Built-in themes plus any .json themes people saved or were given.</summary>
    public static List<Theme> All()
    {
        var themes = BuiltIn.ToList();
        if (Directory.Exists(ThemesFolder))
        {
            foreach (var file in Directory.EnumerateFiles(ThemesFolder, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<Theme>(File.ReadAllText(file)) is { } theme && !string.IsNullOrWhiteSpace(theme.Name))
                    {
                        themes.RemoveAll(t => t.Name.Equals(theme.Name, StringComparison.OrdinalIgnoreCase));
                        themes.Add(theme);
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                }
            }
        }

        return themes;
    }

    public static Theme Find(string? name) => All().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? BuiltIn[0];

    /// <summary>Saves a theme as a shareable file; returns its path.</summary>
    public static string Save(Theme theme)
    {
        Directory.CreateDirectory(ThemesFolder);
        var safe = string.Concat(theme.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = Path.Combine(ThemesFolder, safe + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(theme, JsonOptions));
        return path;
    }

    /// <summary>Copies a theme file someone gave you into your themes; returns the theme.</summary>
    public static Theme Import(string file)
    {
        var theme = JsonSerializer.Deserialize<Theme>(File.ReadAllText(file)) ?? throw new InvalidDataException("Not a theme file");
        Save(theme);
        return theme;
    }

    public Theme Copy(string name) => new()
    {
        Name = name, Description = Description, Accent = Accent, Active = Active, Ink = Ink, Base = Base, Shadow = Shadow,
        Font = Font, Tint = Tint, Outline = Outline, Glass = Glass, WatchColour = WatchColour
    };
}
