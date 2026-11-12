using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DesktopOrbit.ShellIntegration;

/// <summary>A place on the World watch: its own name, time zone and position (for the weather).</summary>
public sealed class Place
{
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>IANA time zone, e.g. "Europe/Malta".</summary>
    public string TimeZone { get; set; } = string.Empty;

    [JsonIgnore]
    public string Key => $"{Latitude:0.00},{Longitude:0.00}";
}

public sealed record Weather(double Temperature, int Code, bool IsDay, double Wind, int Humidity, double TodayHigh, double TodayLow, int TodayRain, double TomorrowHigh, double TomorrowLow, int TomorrowCode, DateTime ReadAt)
{
    /// <summary>Strong wind (km/h) is mentioned even when it's also cloudy or sunny.</summary>
    public bool IsWindy => Wind >= 30;

    /// <summary>What it's like right now, e.g. "cloudy and windy", "light rain", "snow".</summary>
    public string Conditions => IsWindy && Describe(Code) is var sky and not "—" ? $"{sky}, windy" : Describe(Code);

    public string Describe(int code) => code switch
    {
        0 => "clear",
        1 => "mostly clear",
        2 => "partly cloudy",
        3 => "cloudy",
        45 or 48 => "fog",
        >= 51 and <= 57 => "drizzle",
        >= 61 and <= 67 => "rain",
        >= 71 and <= 77 => "snow",
        >= 80 and <= 82 => "showers",
        85 or 86 => "snow showers",
        >= 95 => "thunderstorms",
        _ => "—"
    };

    public string Summary => $"{Math.Round(Temperature)}° {Conditions}";

    public string Forecast =>
        $"today {Math.Round(TodayLow)}–{Math.Round(TodayHigh)}°{(TodayRain > 0 ? $", rain {TodayRain}%" : string.Empty)} · tomorrow {Math.Round(TomorrowLow)}–{Math.Round(TomorrowHigh)}° {Describe(TomorrowCode)}";
}

/// <summary>
/// Times and weather for your places (your own city by default; anyone can add more).
/// Weather comes from Open-Meteo (free, no account); only the places' positions are sent.
/// </summary>
public sealed class WorldService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Dictionary<string, Weather> _weather = [];
    private readonly HashSet<string> _loading = [];

    public WorldService() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("TitaniumOrbitalLauncher/2.0");

    /// <summary>Raised (on a background thread) when new weather arrives.</summary>
    public event Action? Updated;

    /// <summary>
    /// Starting place for a new person: the city of this PC's own time zone (e.g. "Europe/Rome" → Rome).
    /// Its position is looked up on first start (see <see cref="ResolveAsync"/>).
    /// </summary>
    public static List<Place> LocalPlaces()
    {
        var zone = TimeZoneInfo.Local;
        var iana = zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var converted) ? converted : null;
        if (string.IsNullOrEmpty(iana) || !iana.Contains('/'))
        {
            return [];
        }

        var city = iana[(iana.LastIndexOf('/') + 1)..].Replace('_', ' ');
        return [new Place { Name = city, TimeZone = iana }];
    }

    /// <summary>Fills in the map position of places that only have a name and time zone (first start).</summary>
    public async Task<bool> ResolveAsync(Place place)
    {
        if (place.Latitude != 0 || place.Longitude != 0)
        {
            return false;
        }

        var found = await FindAsync(place.Name, CancellationToken.None);
        var match = found.FirstOrDefault(p => p.TimeZone == place.TimeZone) ?? found.FirstOrDefault();
        if (match is null)
        {
            return false;
        }

        place.Latitude = match.Latitude;
        place.Longitude = match.Longitude;
        place.Country = match.Country;
        return true;
    }

    /// <summary>Example places (Malta and the UK).</summary>
    public static List<Place> DefaultPlaces() =>
    [
        new() { Name = "Malta", Country = "Malta", Latitude = 35.8997, Longitude = 14.5148, TimeZone = "Europe/Malta" },
        new() { Name = "UK", Country = "United Kingdom", Latitude = 51.5072, Longitude = -0.1276, TimeZone = "Europe/London" }
    ];

    /// <summary>The time now in a place (falls back to this PC's time if the zone is unknown).</summary>
    public static DateTime TimeIn(Place place)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(place.TimeZone));
        }
        catch (Exception)
        {
            return DateTime.Now;
        }
    }

    /// <summary>"+1h", "−5h", or "" when it's the same as this PC.</summary>
    public static string Difference(Place place)
    {
        var hours = (TimeIn(place) - DateTime.Now).TotalHours;
        var rounded = Math.Round(hours * 2) / 2; // some zones are half an hour off
        return Math.Abs(rounded) < 0.25 ? string.Empty : $"{(rounded > 0 ? "+" : "−")}{Math.Abs(rounded).ToString("0.#", CultureInfo.InvariantCulture)}h";
    }

    /// <summary>Last known weather; starts a refresh in the background when it's missing or over 30 minutes old.</summary>
    public Weather? WeatherFor(Place place)
    {
        Weather? known;
        lock (_weather)
        {
            _weather.TryGetValue(place.Key, out known);
            if ((known is null || DateTime.Now - known.ReadAt > TimeSpan.FromMinutes(30)) && _loading.Add(place.Key))
            {
                _ = RefreshAsync(place);
            }
        }

        return known;
    }

    public async Task<Weather?> RefreshAsync(Place place)
    {
        try
        {
            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={place.Latitude}&longitude={place.Longitude}&current=temperature_2m,weather_code,is_day,wind_speed_10m,relative_humidity_2m&daily=temperature_2m_max,temperature_2m_min,weather_code,precipitation_probability_max&timezone=auto&forecast_days=2");
            var data = await _http.GetFromJsonAsync<ForecastResponse>(url);
            if (data?.Current is null || data.Daily is null || data.Daily.Max.Length < 2)
            {
                return null;
            }

            var weather = new Weather(
                data.Current.Temperature, data.Current.Code, data.Current.IsDay == 1, data.Current.Wind, data.Current.Humidity,
                data.Daily.Max[0], data.Daily.Min[0], data.Daily.Rain.ElementAtOrDefault(0) ?? 0,
                data.Daily.Max[1], data.Daily.Min[1], data.Daily.Codes[1], DateTime.Now);
            lock (_weather)
            {
                _weather[place.Key] = weather;
            }

            Updated?.Invoke();
            return weather;
        }
        catch (Exception)
        {
            return null; // offline: keep the last known weather
        }
        finally
        {
            lock (_weather)
            {
                _loading.Remove(place.Key);
            }
        }
    }

    /// <summary>Finds cities by name, e.g. "rome" → Rome, Italy (Europe/Rome).</summary>
    public async Task<List<Place>> FindAsync(string name, CancellationToken cancellation)
    {
        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=6&language=en";
            var data = await _http.GetFromJsonAsync<GeocodeResponse>(url, cancellation);
            return (data?.Results ?? [])
                .Where(r => !string.IsNullOrEmpty(r.Timezone))
                .Select(r => new Place
                {
                    Name = r.Name,
                    Country = string.Join(", ", new[] { r.Admin1, r.Country }.Where(s => !string.IsNullOrEmpty(s) && s != r.Name)),
                    Latitude = r.Latitude,
                    Longitude = r.Longitude,
                    TimeZone = r.Timezone!
                })
                .ToList();
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return [];
        }
    }

    private sealed class ForecastResponse
    {
        [JsonPropertyName("current")] public CurrentData? Current { get; set; }
        [JsonPropertyName("daily")] public DailyData? Daily { get; set; }
    }

    private sealed class CurrentData
    {
        [JsonPropertyName("temperature_2m")] public double Temperature { get; set; }
        [JsonPropertyName("weather_code")] public int Code { get; set; }
        [JsonPropertyName("is_day")] public int IsDay { get; set; }
        [JsonPropertyName("wind_speed_10m")] public double Wind { get; set; }
        [JsonPropertyName("relative_humidity_2m")] public int Humidity { get; set; }
    }

    private sealed class DailyData
    {
        [JsonPropertyName("temperature_2m_max")] public double[] Max { get; set; } = [];
        [JsonPropertyName("temperature_2m_min")] public double[] Min { get; set; } = [];
        [JsonPropertyName("weather_code")] public int[] Codes { get; set; } = [];
        [JsonPropertyName("precipitation_probability_max")] public int?[] Rain { get; set; } = [];
    }

    private sealed class GeocodeResponse
    {
        [JsonPropertyName("results")] public List<GeocodeResult>? Results { get; set; }
    }

    private sealed class GeocodeResult
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("admin1")] public string? Admin1 { get; set; }
        [JsonPropertyName("latitude")] public double Latitude { get; set; }
        [JsonPropertyName("longitude")] public double Longitude { get; set; }
        [JsonPropertyName("timezone")] public string? Timezone { get; set; }
    }
}
