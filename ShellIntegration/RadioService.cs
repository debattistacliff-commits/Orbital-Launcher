using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopOrbit.ShellIntegration;

/// <summary>A station from the Radio Browser directory, trimmed to what the ring needs.</summary>
public sealed class Station
{
    [JsonPropertyName("stationuuid")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url_resolved")] public string StreamUrl { get; set; } = string.Empty;
    [JsonPropertyName("favicon")] public string Favicon { get; set; } = string.Empty;
    [JsonPropertyName("countrycode")] public string CountryCode { get; set; } = string.Empty;
    [JsonPropertyName("country")] public string Country { get; set; } = string.Empty;
    [JsonPropertyName("tags")] public string Tags { get; set; } = string.Empty;
    [JsonPropertyName("codec")] public string Codec { get; set; } = string.Empty;
    [JsonPropertyName("bitrate")] public int Bitrate { get; set; }
    [JsonPropertyName("hls")] public int Hls { get; set; }
    [JsonPropertyName("votes")] public int Votes { get; set; }

    [JsonIgnore]
    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : "Station";

    [JsonIgnore]
    public string Badge => string.Join(" · ", new[] { CountryCode, Bitrate > 0 ? $"{Bitrate}k" : string.Empty }.Where(s => s.Length > 0));

    [JsonIgnore]
    public string Genre => Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    /// <summary>Windows' built-in player handles MP3 and AAC streams; skip playlists (HLS) and other codecs.</summary>
    [JsonIgnore]
    public bool IsPlayable =>
        Hls == 0 && !string.IsNullOrWhiteSpace(StreamUrl) &&
        (Codec.Equals("MP3", StringComparison.OrdinalIgnoreCase) || Codec.StartsWith("AAC", StringComparison.OrdinalIgnoreCase));
}

public enum RadioState
{
    Stopped,
    Connecting,
    Playing
}

/// <summary>
/// Finds stations (with server failover and a local cache) and plays them in the background,
/// independent of the classic dashboard.
/// </summary>
public sealed class RadioService
{
    private const int MaxRecent = 8;

    // Radio Browser runs several mirrors; if one is down the next is tried.
    private static readonly string[] Servers =
    [
        "https://de1.api.radio-browser.info/",
        "https://de2.api.radio-browser.info/",
        "https://fi1.api.radio-browser.info/",
        "https://nl1.api.radio-browser.info/",
        "https://at1.api.radio-browser.info/",
        "https://all.api.radio-browser.info/"
    ];

    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "radio");

    private readonly Dictionary<string, IReadOnlyList<Station>> _top = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<IReadOnlyList<Station>>> _topLoading = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(7) };
    private readonly Dictionary<string, Task<ImageSource?>> _logos = new(StringComparer.OrdinalIgnoreCase);
    private readonly RingSettings _settings;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private readonly object _playGate = new();
    private NAudio.Wave.WasapiOut? _output;
    private NAudio.Wave.MediaFoundationReader? _reader;
    private NAudio.Wave.SampleProviders.VolumeSampleProvider? _volume;
    private int _generation;
    private bool _retried;
    private int _serverIndex;

    public RadioService(RingSettings settings)
    {
        _settings = settings;
        _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TitaniumOrbitalLauncher/2.0");
    }

    /// <summary>Live sound levels of the playing station, for the lights around the clock.</summary>
    public SpectrumTap Spectrum { get; } = new();

    /// <summary>Raised on the UI thread when playback starts, stops or fails (message is non-null on failure).</summary>
    public event Action<string?>? StateChanged;

    public Station? Current { get; private set; }

    public RadioState State { get; private set; }

    /// <summary>Country name for a two-letter code; an empty code means the whole world.</summary>
    public static string CountryName(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "World";
        }

        // Short names fit inside a bubble.
        switch (code.ToUpperInvariant())
        {
            case "GB": return "UK";
            case "US": return "USA";
            case "AE": return "UAE";
        }

        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    public int Volume
    {
        get => _settings.RadioVolume;
        set
        {
            _settings.RadioVolume = Math.Clamp(value, 0, 100);
            _settings.Save();
            if (_volume is not null)
            {
                _volume.Volume = Gain(_settings.RadioVolume);
            }
        }
    }

    // Only the radio's own stream is scaled; the Windows volume is never touched.
    private static float Gain(int percent) => (float)Math.Pow(percent / 100.0, 1.6);

    public string StatusText => State switch
    {
        RadioState.Connecting => "connecting…",
        RadioState.Playing => $"▶ {Volume}%",
        _ => "stopped"
    };

    public bool IsFavorite(Station station) => _settings.RadioFavorites.Any(s => s.Id == station.Id);

    public string ToggleFavorite(Station station)
    {
        if (_settings.RadioFavorites.RemoveAll(s => s.Id == station.Id) > 0)
        {
            _settings.Save();
            return $"Removed {station.DisplayName} from favourites";
        }

        _settings.RadioFavorites.Add(station);
        _settings.Save();
        return $"Saved {station.DisplayName} to favourites";
    }

    // ------------------------------------------------------------ playback (UI thread)

    public void Play(Station station)
    {
        Current = station;
        _retried = false;
        StartStream(station);

        _settings.RadioRecent.RemoveAll(s => s.Id == station.Id);
        _settings.RadioRecent.Insert(0, station);
        if (_settings.RadioRecent.Count > MaxRecent)
        {
            _settings.RadioRecent.RemoveRange(MaxRecent, _settings.RadioRecent.Count - MaxRecent);
        }

        _settings.Save();
        _ = CountClickAsync(station.Id);
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        ReleaseOutput();
        Spectrum.Reset();
        SetState(RadioState.Stopped, null);
    }

    /// <summary>
    /// Opens the stream with Media Foundation (MP3 and AAC over http/https) and plays it through WASAPI.
    /// Opening takes about a second, so it runs in the background.
    /// </summary>
    private void StartStream(Station station)
    {
        var generation = Interlocked.Increment(ref _generation);
        ReleaseOutput();
        SetState(RadioState.Connecting, null);

        Task.Run(() =>
        {
            NAudio.Wave.MediaFoundationReader? reader = null;
            NAudio.Wave.WasapiOut? output = null;
            try
            {
                reader = new NAudio.Wave.MediaFoundationReader(station.StreamUrl);
                var samples = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
                var tapped = Spectrum.Attach(samples);
                var volume = new NAudio.Wave.SampleProviders.VolumeSampleProvider(tapped) { Volume = Gain(Volume) };
                output = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200);
                output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(volume));

                lock (_playGate)
                {
                    if (generation != _generation)
                    {
                        throw new OperationCanceledException(); // another station was chosen meanwhile
                    }

                    _reader = reader;
                    _output = output;
                    _volume = volume;
                }

                output.PlaybackStopped += (_, e) => _ui.BeginInvoke(() => OnStreamStopped(station, generation, e.Exception));
                output.Play();
                _ui.BeginInvoke(() =>
                {
                    if (generation == _generation)
                    {
                        SetState(RadioState.Playing, null);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                output?.Dispose();
                reader?.Dispose();
            }
            catch (Exception ex)
            {
                output?.Dispose();
                reader?.Dispose();
                _ui.BeginInvoke(() =>
                {
                    if (generation == _generation)
                    {
                        SetState(RadioState.Stopped, $"{station.DisplayName} is offline or can't be played ({ex.Message.Trim()})");
                    }
                });
            }
        });
    }

    private void OnStreamStopped(Station station, int generation, Exception? error)
    {
        if (generation != _generation || State == RadioState.Stopped)
        {
            return; // stopped on purpose, or replaced by another station
        }

        // Streams sometimes drop for a moment: reconnect once before giving up.
        if (!_retried)
        {
            _retried = true;
            StartStream(station);
            return;
        }

        ReleaseOutput();
        Spectrum.Reset();
        SetState(RadioState.Stopped, $"{station.DisplayName} stopped streaming{(error is null ? string.Empty : $" ({error.Message.Trim()})")}");
    }

    private void ReleaseOutput()
    {
        NAudio.Wave.WasapiOut? output;
        NAudio.Wave.MediaFoundationReader? reader;
        lock (_playGate)
        {
            output = _output;
            reader = _reader;
            _output = null;
            _reader = null;
            _volume = null;
        }

        if (output is null && reader is null)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                output?.Stop();
                output?.Dispose();
                reader?.Dispose();
            }
            catch (Exception)
            {
                // Closing a dead network stream can throw; nothing to do.
            }
        });
    }

    private void SetState(RadioState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(message);
    }

    // ------------------------------------------------------------ directory

    /// <summary>Most-played stations in a country ("" = the whole world), if already loaded.</summary>
    public IReadOnlyList<Station>? CachedTop(string country)
    {
        lock (_top)
        {
            return _top.GetValueOrDefault(country);
        }
    }

    /// <summary>Most-played stations in a country ("" = the whole world). Kept on disk for a day so the ring opens instantly.</summary>
    public Task<IReadOnlyList<Station>> GetTopAsync(string country)
    {
        lock (_top)
        {
            if (!_topLoading.TryGetValue(country, out var task))
            {
                task = LoadTopAsync(country);
                _topLoading[country] = task;
            }

            return task;
        }
    }

    private async Task<IReadOnlyList<Station>> LoadTopAsync(string country)
    {
        var cacheFile = Path.Combine(CacheFolder, $"top-{(country.Length == 0 ? "world" : country.ToLowerInvariant())}.json");
        List<Station>? stations = null;
        try
        {
            if (File.Exists(cacheFile) && File.GetLastWriteTimeUtc(cacheFile) > DateTime.UtcNow.AddDays(-1))
            {
                stations = JsonSerializer.Deserialize<List<Station>>(await File.ReadAllTextAsync(cacheFile));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        if (stations is not { Count: > 0 })
        {
            var filter = country.Length == 0 ? string.Empty : $"countrycode={country}&";
            stations = (await QueryAsync($"json/stations/search?{filter}order=clickcount&reverse=true&hidebroken=true&limit=80"))
                .Where(s => s.IsPlayable)
                .DistinctBy(s => s.Name.Trim().ToLowerInvariant()) // the directory lists some stations twice
                .Take(24)
                .ToList();

            if (stations.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(CacheFolder);
                    await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(stations));
                }
                catch (IOException)
                {
                }
            }
        }

        lock (_top)
        {
            _top[country] = stations;
            if (stations.Count == 0)
            {
                _topLoading.Remove(country); // allow a retry later
            }
        }

        return stations;
    }

    /// <summary>Searches by station name, genre and country at once.</summary>
    public async Task<List<Station>> SearchAsync(string term, CancellationToken cancellation)
    {
        var escaped = Uri.EscapeDataString(term.Trim());
        const string common = "order=votes&reverse=true&hidebroken=true&limit=40";
        var results = await Task.WhenAll(
            QueryAsync($"json/stations/search?name={escaped}&{common}", cancellation),
            QueryAsync($"json/stations/search?tag={escaped}&{common}", cancellation),
            QueryAsync($"json/stations/search?country={escaped}&{common}", cancellation));

        return results.SelectMany(r => r)
            .Where(s => s.IsPlayable)
            .DistinctBy(s => s.Id)
            .OrderByDescending(s => s.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s => s.Votes)
            .Take(8)
            .ToList();
    }

    private async Task<List<Station>> QueryAsync(string path, CancellationToken cancellation = default)
    {
        for (var attempt = 0; attempt < Servers.Length; attempt++)
        {
            var server = Servers[(_serverIndex + attempt) % Servers.Length];
            try
            {
                var stations = await _http.GetFromJsonAsync<List<Station>>(server + path, cancellation) ?? [];
                _serverIndex = (_serverIndex + attempt) % Servers.Length; // stick with the server that answered
                return stations;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Try the next mirror.
            }
        }

        return [];
    }

    /// <summary>Radio Browser asks apps to report plays; it helps rank working stations.</summary>
    private async Task CountClickAsync(string id)
    {
        try
        {
            using var _ = await _http.GetAsync(Servers[_serverIndex] + "json/url/" + id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    // ------------------------------------------------------------ logos

    public Task<ImageSource?> GetLogoAsync(Station station)
    {
        if (string.IsNullOrWhiteSpace(station.Favicon) || !Uri.TryCreate(station.Favicon, UriKind.Absolute, out var uri))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        lock (_logos)
        {
            if (!_logos.TryGetValue(station.Favicon, out var task))
            {
                task = DownloadLogoAsync(uri);
                _logos[station.Favicon] = task;
            }

            return task;
        }
    }

    private async Task<ImageSource?> DownloadLogoAsync(Uri uri)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(uri);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // Many stations have broken or unsupported logos (e.g. .ico, .svg); the radio glyph is used instead.
        }
    }
}
