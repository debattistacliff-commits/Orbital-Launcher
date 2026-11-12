using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopOrbit.ShellIntegration;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfPoint = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DesktopOrbit.Ring;

/// <summary>
/// The animated Weather watch for your first place: sun (or moon), drifting clouds, falling rain or snow,
/// lightning, wind streaks or fog, with temperature, conditions, humidity, wind and today's high / low.
/// </summary>
public sealed partial class RingWindow
{
    private enum Sky
    {
        Clear,
        PartlyCloudy,
        Cloudy,
        Fog,
        Rain,
        Snow,
        Storm
    }

    private const double SkyWidth = 112;
    private const double SkyHeight = 54;

    private readonly Canvas _sky = new() { Width = SkyWidth, Height = SkyHeight, ClipToBounds = true };
    private readonly TextBlock _weatherPlace = Label(8.5, FontWeights.SemiBold, 0.75);
    private readonly TextBlock _weatherTemp = Label(24, FontWeights.SemiBold, 1);
    private readonly TextBlock _weatherText = Label(9.5, FontWeights.Normal, 0.95);
    private readonly TextBlock _weatherDetail = Label(8, FontWeights.Normal, 0.8);
    private readonly TextBlock _hubWeather = Label(10, FontWeights.Normal, 0.9);
    private readonly DispatcherTimer _skyTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Random _random = new();
    private readonly List<(Shape Shape, double Speed, double Drift)> _particles = [];
    private readonly List<(UIElement Element, double Speed)> _drifters = [];
    private readonly Rectangle _lightning = new() { Width = SkyWidth, Height = SkyHeight, Fill = Brushes.White, Opacity = 0, IsHitTestVisible = false };
    private readonly RotateTransform _sunSpin = new();
    private (Sky Sky, bool Windy, bool Day)? _skyBuiltFor;
    private double _skyTime;

    private UIElement WeatherFace()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 14) };
        stack.Children.Add(_weatherPlace);
        stack.Children.Add(_sky);
        _weatherTemp.Margin = new Thickness(0, -4, 0, -3);
        stack.Children.Add(_weatherTemp);
        stack.Children.Add(_weatherText);
        stack.Children.Add(_weatherDetail);
        _skyTimer.Tick += (_, _) => AnimateSky();
        return stack;
    }

    private Place? HomePlace => _settings.Places.FirstOrDefault();

    private void UpdateWeatherFace()
    {
        var place = HomePlace;
        if (place is null)
        {
            _weatherPlace.Text = "WEATHER";
            _weatherTemp.Text = "–";
            _weatherText.Text = "Add a place in System › World";
            _weatherDetail.Text = string.Empty;
            return;
        }

        var weather = _world.WeatherFor(place);
        _weatherPlace.Text = place.Name.ToUpperInvariant();
        if (weather is null)
        {
            _weatherTemp.Text = "…";
            _weatherText.Text = "loading the weather";
            _weatherDetail.Text = string.Empty;
            return;
        }

        _weatherTemp.Text = $"{Math.Round(weather.Temperature)}°";
        _weatherText.Text = weather.Conditions;
        _weatherDetail.Text = $"{weather.Humidity}% humid · wind {Math.Round(weather.Wind)} km/h\n{Math.Round(weather.TodayLow)}° to {Math.Round(weather.TodayHigh)}° today";

        var look = (SkyFor(weather.Code), weather.IsWindy, weather.IsDay);
        if (_skyBuiltFor != look)
        {
            BuildSky(look.Item1, look.IsWindy, look.IsDay);
            _skyBuiltFor = look;
        }
    }

    private static Sky SkyFor(int code) => code switch
    {
        0 or 1 => Sky.Clear,
        2 => Sky.PartlyCloudy,
        3 => Sky.Cloudy,
        45 or 48 => Sky.Fog,
        >= 71 and <= 77 or 85 or 86 => Sky.Snow,
        >= 95 => Sky.Storm,
        >= 51 and <= 82 => Sky.Rain,
        _ => Sky.Cloudy
    };

    /// <summary>Starts or stops the animation: it only runs while the Weather watch is showing.</summary>
    private void UpdateSkyRunning()
    {
        var on = IsVisible && _face == WatchFace.Weather;
        if (on && !_skyTimer.IsEnabled && SystemParameters.ClientAreaAnimation)
        {
            _skyTimer.Start();
        }
        else if (!on && _skyTimer.IsEnabled)
        {
            _skyTimer.Stop();
        }
    }

    // ---------------------------------------------------------------- building the scene

    private void BuildSky(Sky sky, bool windy, bool day)
    {
        _sky.Children.Clear();
        _particles.Clear();
        _drifters.Clear();

        if (sky is Sky.Clear or Sky.PartlyCloudy)
        {
            _sky.Children.Add(day ? Sun(new WpfPoint(SkyWidth / 2 - (sky == Sky.PartlyCloudy ? 14 : 0), 26)) : Moon(new WpfPoint(SkyWidth / 2 - (sky == Sky.PartlyCloudy ? 14 : 0), 26)));
        }

        var clouds = sky switch
        {
            Sky.PartlyCloudy => 1,
            Sky.Cloudy or Sky.Fog => 3,
            Sky.Rain or Sky.Snow or Sky.Storm => 2,
            _ => 0
        };
        for (var i = 0; i < clouds; i++)
        {
            var dark = sky is Sky.Rain or Sky.Storm;
            var cloud = Cloud(dark ? Color.FromRgb(0xA8, 0xB0, 0xBC) : Color.FromRgb(0xF2, 0xF4, 0xF8), 0.7 + 0.15 * i);
            Canvas.SetLeft(cloud, i * 40 - 10 + (sky == Sky.PartlyCloudy ? 34 : 0));
            Canvas.SetTop(cloud, 4 + (i % 2) * 10);
            _sky.Children.Add(cloud);
            _drifters.Add((cloud, 0.12 + 0.05 * i));
        }

        if (sky == Sky.Fog)
        {
            for (var i = 0; i < 4; i++)
            {
                var band = new Rectangle { Width = 90, Height = 4, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Color.FromArgb(0x90, 0xE0, 0xE4, 0xEA)) };
                Canvas.SetLeft(band, i * 22 - 20);
                Canvas.SetTop(band, 30 + i * 6);
                _sky.Children.Add(band);
                _drifters.Add((band, 0.25 + 0.1 * (i % 2)));
            }
        }

        if (sky is Sky.Rain or Sky.Storm)
        {
            for (var i = 0; i < 22; i++)
            {
                var drop = new Line { X1 = 0, Y1 = 0, X2 = -1.5, Y2 = 6, Stroke = new SolidColorBrush(Color.FromArgb(0xD0, 0x7F, 0xC4, 0xFF)), StrokeThickness = 1.4, StrokeEndLineCap = PenLineCap.Round };
                Canvas.SetLeft(drop, _random.NextDouble() * SkyWidth);
                Canvas.SetTop(drop, 18 + _random.NextDouble() * (SkyHeight - 18));
                _sky.Children.Add(drop);
                _particles.Add((drop, 2.2 + _random.NextDouble() * 1.2, windy ? -0.8 : -0.2));
            }
        }

        if (sky == Sky.Snow)
        {
            for (var i = 0; i < 20; i++)
            {
                var size = 2 + _random.NextDouble() * 2;
                var flake = new Ellipse { Width = size, Height = size, Fill = Brushes.White };
                Canvas.SetLeft(flake, _random.NextDouble() * SkyWidth);
                Canvas.SetTop(flake, 16 + _random.NextDouble() * (SkyHeight - 16));
                _sky.Children.Add(flake);
                _particles.Add((flake, 0.5 + _random.NextDouble() * 0.5, 0));
            }
        }

        if (windy)
        {
            for (var i = 0; i < 4; i++)
            {
                var streak = new Path
                {
                    Data = Geometry.Parse("M0,4 C10,0 18,8 30,4"),
                    Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                Canvas.SetLeft(streak, i * 34 - 20);
                Canvas.SetTop(streak, 12 + i * 10);
                _sky.Children.Add(streak);
                _drifters.Add((streak, 2.2 + 0.4 * i));
            }
        }

        _lightning.Opacity = 0;
        _sky.Children.Add(_lightning);
    }

    private UIElement Sun(WpfPoint centre)
    {
        var sun = new Canvas { Width = 44, Height = 44, RenderTransform = _sunSpin };
        _sunSpin.CenterX = 22;
        _sunSpin.CenterY = 22;
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            sun.Children.Add(new Line
            {
                X1 = 22 + Math.Cos(angle) * 15, Y1 = 22 + Math.Sin(angle) * 15,
                X2 = 22 + Math.Cos(angle) * 20, Y2 = 22 + Math.Sin(angle) * 20,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3A)), StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }

        var disc = new Ellipse
        {
            Width = 22, Height = 22,
            Fill = new RadialGradientBrush(Color.FromRgb(0xFF, 0xE5, 0x7A), Color.FromRgb(0xFF, 0xA9, 0x1F)),
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0xB0, 0x30), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.9 }
        };
        Canvas.SetLeft(disc, 11);
        Canvas.SetTop(disc, 11);
        sun.Children.Add(disc);
        Canvas.SetLeft(sun, centre.X - 22);
        Canvas.SetTop(sun, centre.Y - 22);
        return sun;
    }

    private static UIElement Moon(WpfPoint centre)
    {
        var moon = new Path
        {
            Data = Geometry.Parse("M14,2 A12,12 0 1 0 26,16 A9,9 0 1 1 14,2 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0xF0, 0xD8)),
            Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 }
        };
        Canvas.SetLeft(moon, centre.X - 14);
        Canvas.SetTop(moon, centre.Y - 16);
        return moon;
    }

    private static UIElement Cloud(Color colour, double scale)
    {
        var cloud = new Path
        {
            Data = Geometry.Parse("M8,22 A7,7 0 0 1 9,8 A10,10 0 0 1 28,6 A8,8 0 0 1 40,14 A7,7 0 0 1 38,22 Z"),
            Fill = new SolidColorBrush(colour),
            Opacity = 0.95,
            RenderTransform = new ScaleTransform(scale, scale),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35, Color = Colors.Black }
        };
        return cloud;
    }

    // ---------------------------------------------------------------- animation

    private void AnimateSky()
    {
        _skyTime += 0.04;
        _sunSpin.Angle = (_sunSpin.Angle + 0.4) % 360;

        foreach (var (element, speed) in _drifters)
        {
            var x = Canvas.GetLeft(element) + speed;
            Canvas.SetLeft(element, x > SkyWidth ? -50 : x);
        }

        foreach (var (shape, speed, drift) in _particles)
        {
            var y = Canvas.GetTop(shape) + speed;
            var x = Canvas.GetLeft(shape) + drift + (shape is Ellipse ? Math.Sin(_skyTime * 2 + y / 7) * 0.35 : 0);
            if (y > SkyHeight)
            {
                y = 14 + _random.NextDouble() * 6;
                x = _random.NextDouble() * SkyWidth;
            }

            if (x < -4)
            {
                x += SkyWidth + 4;
            }

            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);
        }

        // Lightning: an occasional double flash.
        if (_skyBuiltFor?.Sky == Sky.Storm)
        {
            _lightning.Opacity = _lightning.Opacity > 0.01 ? _lightning.Opacity * 0.75 : _random.NextDouble() < 0.012 ? 0.7 : 0;
        }
    }

    // ---------------------------------------------------------------- self-test

    /// <summary>Self-test: draws the Weather watch once per kind of weather, side by side, into a PNG.</summary>
    public void RenderSkiesForTest(string path)
    {
        var original = _face;
        var scenes = new (int Code, bool Windy, bool Day, string Label)[]
        {
            (0, false, true, "sunny"), (2, false, true, "partly cloudy"), (3, false, true, "cloudy"), (61, false, true, "rain"),
            (71, false, true, "snow"), (95, false, true, "storm"), (45, false, true, "fog"), (3, true, true, "cloudy + windy"), (0, false, false, "clear night")
        };
        SetFace(WatchFace.Weather, remember: false);
        const int size = 200;
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        foreach (var scene in scenes)
        {
            ShowSkyForTest(scene.Code, scene.Windy, scene.Day);
            _weatherText.Text = scene.Label;
            _ring.Measure(new System.Windows.Size(Radius * 2, Radius * 2));
            _ring.Arrange(new Rect(0, 0, Radius * 2, Radius * 2));
            _ring.UpdateLayout();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x34)), null, new Rect(0, 0, size, size));
                // Just the watch, from the middle of the ring.
                context.DrawRectangle(new VisualBrush(_ring) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(Radius - size / 2.0, Radius - size / 2.0, size, size) },
                    null, new Rect(0, 0, size, size));
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            frames.Add(bitmap);
        }

        _skyBuiltFor = null;
        SetFace(original, remember: false);

        var strip = new DrawingVisual();
        using (var context = strip.RenderOpen())
        {
            for (var i = 0; i < frames.Count; i++)
            {
                context.DrawImage(frames[i], new Rect(i * size, 0, size, size));
            }
        }

        var all = new System.Windows.Media.Imaging.RenderTargetBitmap(size * frames.Count, size, 96, 96, PixelFormats.Pbgra32);
        all.Render(strip);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Self-test: builds the scene for a given weather code so its picture can be drawn.</summary>
    public string ShowSkyForTest(int code, bool windy, bool day)
    {
        var sky = SkyFor(code);
        BuildSky(sky, windy, day);
        _skyBuiltFor = (sky, windy, day);
        for (var i = 0; i < 25; i++)
        {
            AnimateSky();
        }

        return $"{sky}{(windy ? " + wind" : string.Empty)}{(day ? string.Empty : " (night)")}: {_sky.Children.Count - 1} shapes";
    }
}
