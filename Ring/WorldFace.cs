using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopOrbit.ShellIntegration;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace DesktopOrbit.Ring;

/// <summary>
/// The World watch (time and weather for your places, e.g. Malta and the UK) and System › World,
/// where anyone can add their own cities ("w rome") or remove them.
/// </summary>
public sealed partial class RingWindow
{
    private const int MaxPlaces = 6;
    private const int PlacesOnWatch = 3;

    private readonly WorldService _world = new();
    private readonly StackPanel _worldRows = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private CancellationTokenSource? _placeSearch;
    private List<RingItem> _placeResults = [];
    private string _placeResultsFor = string.Empty;

    // Small weather glyphs (24×24).
    private static string WeatherGlyph(int code) => code switch
    {
        0 or 1 => "M12 7.5a4.5 4.5 0 1 0 0.01 0z M12 1.5v2.5 M12 20v2.5 M1.5 12h2.5 M20 12h2.5 M4.6 4.6l1.8 1.8 M17.6 17.6l1.8 1.8 M4.6 19.4l1.8-1.8 M17.6 6.4l1.8-1.8",
        2 => "M9 5.5a3.5 3.5 0 0 1 6.6 1.3 M7 18h10.5a3.5 3.5 0 0 0 0-7h-.4A5 5 0 0 0 7.5 10 4 4 0 0 0 7 18z",
        3 or 45 or 48 => "M6.5 18h11a4 4 0 0 0 0-8h-.5A5.5 5.5 0 0 0 6.6 9.2 4.4 4.4 0 0 0 6.5 18z",
        >= 71 and <= 77 or 85 or 86 => "M6.5 14h11a4 4 0 0 0 0-8h-.5A5.5 5.5 0 0 0 6.6 5.2 4.4 4.4 0 0 0 6.5 14z M8 18h.01 M12 19h.01 M16 18h.01 M10 21.5h.01 M14 21.5h.01",
        >= 95 => "M6.5 14h11a4 4 0 0 0 0-8h-.5A5.5 5.5 0 0 0 6.6 5.2 4.4 4.4 0 0 0 6.5 14z M12.5 15l-2.5 4h3l-2 4",
        _ => "M6.5 14h11a4 4 0 0 0 0-8h-.5A5.5 5.5 0 0 0 6.6 5.2 4.4 4.4 0 0 0 6.5 14z M8.5 17l-1 3 M12.5 17l-1 3 M16.5 17l-1 3"
    };

    private UIElement WorldFace()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(Label(8.5, FontWeights.SemiBold, 0.7).With("WORLD"));
        stack.Children.Add(_worldRows);
        return stack;
    }

    private void UpdateWorldFace()
    {
        _worldRows.Children.Clear();
        var places = _settings.Places.Take(PlacesOnWatch).ToList();
        if (places.Count == 0)
        {
            _worldRows.Children.Add(Label(9.5, FontWeights.Normal, 0.85).With("Add places in System › World"));
            return;
        }

        var compact = places.Count > 2;
        foreach (var place in places)
        {
            var weather = _world.WeatherFor(place);
            var row = new Grid { Width = 118, Margin = new Thickness(0, compact ? 1 : 3, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var name = Label(compact ? 9.5 : 10.5, FontWeights.SemiBold, 1).With(place.Name);
            name.HorizontalAlignment = HorizontalAlignment.Left;
            name.TextAlignment = TextAlignment.Left;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 64;
            left.Children.Add(name);

            var detail = new StackPanel { Orientation = Orientation.Horizontal };
            if (weather is not null)
            {
                var glyph = GlyphPath(WeatherGlyph(weather.Code));
                glyph.Width = glyph.Height = 24;
                glyph.StrokeThickness = 1.6;
                glyph.RenderTransform = new ScaleTransform(0.5, 0.5);
                glyph.Margin = new Thickness(0, 0, -10, -12);
                detail.Children.Add(glyph);
            }

            var temp = Label(compact ? 8.5 : 9.5, FontWeights.Normal, 0.85).With(weather is null ? "…" : $"{Math.Round(weather.Temperature)}°");
            temp.Margin = new Thickness(2, 0, 0, 0);
            detail.Children.Add(temp);
            left.Children.Add(detail);
            row.Children.Add(left);

            var time = Label(compact ? 15 : 18, FontWeights.SemiBold, 1).With(WorldService.TimeIn(place).ToString("HH:mm"));
            time.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(time, 1);
            row.Children.Add(time);
            _worldRows.Children.Add(row);
        }
    }

    // ---------------------------------------------------------------- System › World

    private RingItem WorldMenuItem() => new()
    {
        Title = "World",
        Glyph = Glyphs.Web,
        Hint = "Time and weather for your places · add your own cities",
        Status = () => _settings.Places.FirstOrDefault() is { } first ? $"{WorldService.TimeIn(first):HH:mm} {first.Name}" : "add places",
        ClosesRing = false,
        Rebuild = true,
        Activate = () => { _systemView = "world"; _selected = 1; }
    };

    private List<RingItem> WorldItems()
    {
        var items = new List<RingItem>
        {
            new()
            {
                Title = "Back",
                Glyph = Glyphs.Back,
                Hint = "Back to System (Esc)",
                ClosesRing = false,
                Rebuild = true,
                Activate = () => { _systemView = null; }
            }
        };
        items.AddRange(_settings.Places.Take(MaxPlaces).Select(PlaceItem));
        if (_settings.Places.Count < MaxPlaces)
        {
            items.Add(new RingItem
            {
                Title = "Add place",
                Glyph = Glyphs.Plus,
                Hint = "Type w and a city, e.g. \"w rome\", \"w new york\"",
                ClosesRing = false,
                Activate = () => SetQuery("w ")
            });
        }

        return items;
    }

    private RingItem PlaceItem(Place place)
    {
        var weather = _world.WeatherFor(place);
        var difference = WorldService.Difference(place);
        return new RingItem
        {
            Title = place.Name,
            Glyph = WeatherGlyph(weather?.Code ?? 3),
            Subtitle = $"{place.Country} · {WorldService.TimeIn(place):HH:mm}{(difference.Length > 0 ? $" ({difference})" : string.Empty)}{(weather is null ? string.Empty : " · " + weather.Summary)}",
            Status = () => $"{WorldService.TimeIn(place):HH:mm} · {(_world.WeatherFor(place) is { } w ? $"{Math.Round(w.Temperature)}°" : "…")}",
            Hint = (weather is null ? "Weather loading…" : $"{place.Name}: {weather.Summary}, {weather.Forecast}")
                   + (difference.Length > 0 ? $" · {difference} from here" : string.Empty) + " · right-click: remove",
            CountKey = "place:" + place.Key,
            ClosesRing = false,
            Activate = () =>
            {
                SetFace(WatchFace.World, remember: false);
                Flash(_world.WeatherFor(place) is { } w ? $"{place.Name} {WorldService.TimeIn(place):HH:mm} · {w.Summary} · {w.Forecast}" : $"{place.Name} {WorldService.TimeIn(place):HH:mm}");
            },
            Secondary = () =>
            {
                _settings.Places.RemoveAll(p => p.Key == place.Key);
                _settings.Save();
                Refresh();
                return $"Removed {place.Name} from your places";
            }
        };
    }

    /// <summary>"w rome": finds cities to add.</summary>
    private List<RingItem> PlaceSearch(string term)
    {
        term = term.Trim();
        if (term.Length < 2)
        {
            return [Info("Add a place", Glyphs.Web, "Type a city after w, e.g. \"w rome\" or \"w sydney\"")];
        }

        if (string.Equals(_placeResultsFor, term, StringComparison.OrdinalIgnoreCase))
        {
            return _placeResults.Count > 0 ? _placeResults : [Info("No places", Glyphs.Web, $"No city called \"{term}\" was found")];
        }

        _placeSearch?.Cancel();
        var cancellation = _placeSearch = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cancellation.Token);
                var found = await _world.FindAsync(term, cancellation.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _placeResultsFor = term;
                    _placeResults = found.Select(AddPlaceItem).ToList();
                    if (IsPlaceQuery(_query))
                    {
                        Refresh();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });

        return [Info("Searching…", Glyphs.Search, $"Looking for \"{term}\"")];
    }

    private RingItem AddPlaceItem(Place place) => new()
    {
        Title = $"Add {place.Name}",
        Glyph = Glyphs.Plus,
        Subtitle = $"{place.Country} · {WorldService.TimeIn(place):HH:mm} there now",
        Hint = "Adds it to your World watch and System › World",
        ClosesRing = false,
        Activate = () => Flash(AddPlace(place))
    };

    public string AddPlace(Place place)
    {
        if (_settings.Places.Any(p => p.Key == place.Key))
        {
            return $"{place.Name} is already one of your places";
        }

        if (_settings.Places.Count >= MaxPlaces)
        {
            return $"You have {MaxPlaces} places. Right-click one in System › World to remove it first";
        }

        _settings.Places.Add(place);
        _settings.Save();
        _ = _world.RefreshAsync(place);
        _query = string.Empty;
        _sector = Array.FindIndex(Sectors, s => s.Name == "system");
        _systemView = "world";
        Refresh();
        return $"Added {place.Name} ({WorldService.TimeIn(place):HH:mm} there now)";
    }

    private static bool IsPlaceQuery(string query) =>
        query.StartsWith("w ", StringComparison.OrdinalIgnoreCase) || query.StartsWith("weather ", StringComparison.OrdinalIgnoreCase)
        || query.StartsWith("place ", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- self-test

    public Task<List<Place>> FindPlacesForTest(string name) => _world.FindAsync(name, CancellationToken.None);

    public Task<Weather?> WeatherForTest(Place place) => _world.RefreshAsync(place);
}
