using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DesktopOrbit.ShellIntegration;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using WpfPoint = System.Windows.Point;

namespace DesktopOrbit.Ring;

/// <summary>
/// The watch in the centre of the ring. Five faces, switched by scrolling over it or clicking the dots:
/// digital, classic, glow, calendar, and radio (with a play / stop button).
/// </summary>
public sealed partial class RingWindow
{
    private enum WatchFace
    {
        Digital,
        Classic,
        Classic24,
        Glow,
        Calendar,
        Timer,
        Radio,
        Volume,
        World,
        Weather
    }

    private static readonly string[] FaceNames = ["Digital", "Classic 12-hour", "Classic 24-hour", "Glow", "Calendar", "Timer", "Radio", "Volume", "World", "Weather"];
    private const int FaceCount = 10;

    private readonly TextBlock _timerLabel = Label(8.5, FontWeights.SemiBold, 0.75);
    private readonly TextBlock _timerTime = Label(23, FontWeights.SemiBold, 1);
    private readonly TextBlock _timerSub = Label(9.5, FontWeights.Normal, 0.85);
    private readonly Grid _timerButton = new() { Width = 40, Height = 40, Cursor = Cursors.Hand };
    private readonly Ellipse _timerButtonFace = new();
    private readonly Path _timerButtonGlyph = new() { Fill = Theme.InkBrush, IsHitTestVisible = false };
    private readonly Path _timerArc = new() { StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
    private static readonly Geometry PauseGlyph = Geometry.Parse("M0,0 H4.5 V14 H0 Z M8.5,0 H13 V14 H8.5 Z");
    private const double Dial = HubGlass * 2;

    private Ellipse _hubGlass = null!;
    private readonly Grid _faceHost = new() { IsHitTestVisible = false, Width = Dial, Height = Dial };
    private readonly UIElement[] _faces = new UIElement[FaceCount];
    private readonly Ellipse[] _faceDots = new Ellipse[FaceCount];
    private WatchFace _face;

    private readonly RotateTransform _classicHour = new(), _classicMinute = new(), _classicSecond = new();
    private readonly RotateTransform _dayHour = new(), _dayMinute = new(), _daySecond = new();
    private readonly RotateTransform _glowHour = new(), _glowMinute = new(), _glowSecond = new();
    private readonly TextBlock _calDay = Label(10.5, FontWeights.SemiBold, 1);
    private readonly TextBlock _calNumber = Label(40, FontWeights.Light, 1);
    private readonly TextBlock _calMonth = Label(10.5, FontWeights.Normal, 0.85);
    private readonly StackPanel _calWeek = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock _radioName = Label(12, FontWeights.SemiBold, 1);
    private readonly TextBlock _radioState = Label(9.5, FontWeights.Normal, 0.85);
    private readonly Grid _radioButton = new() { Width = 40, Height = 40, Cursor = Cursors.Hand };
    private readonly Path _radioButtonGlyph = new() { Fill = Theme.InkBrush, IsHitTestVisible = false };
    private readonly Ellipse _radioButtonFace = new();

    // Hint line under the ring (what the highlighted item does, or a short message).
    private readonly Grid _hintHost = new() { Width = 520, Height = 30, IsHitTestVisible = false };
    private readonly Border _hintPill = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 4, 12, 5),
        Background = new SolidColorBrush(Theme.BaseA(0xB0))
    };
    private readonly TextBlock _hintText = new() { FontSize = 11.5, Foreground = Theme.InkBrush, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 490 };

    // Watch colours, from the glowing widgets in the reference picture. Glass = see-through like the ring.
    private static readonly (string Name, Color[]? Stops)[] DialColours =
    [
        ("Glass", null),
        ("Purple", [Color.FromRgb(0x1C, 0x06, 0x2C), Color.FromRgb(0x4A, 0x14, 0x7A), Color.FromRgb(0xC2, 0x4F, 0xE6), Color.FromRgb(0xF2, 0x9C, 0xF7)]),
        ("Ocean", [Color.FromRgb(0x03, 0x10, 0x1E), Color.FromRgb(0x0D, 0x45, 0x80), Color.FromRgb(0x3F, 0xA4, 0xF2), Color.FromRgb(0xA6, 0xDC, 0xFF)]),
        ("Lime", [Color.FromRgb(0x08, 0x10, 0x03), Color.FromRgb(0x2B, 0x45, 0x08), Color.FromRgb(0xA9, 0xD8, 0x22), Color.FromRgb(0xEE, 0xFF, 0x96)]),
        ("Sunset", [Color.FromRgb(0x1E, 0x05, 0x02), Color.FromRgb(0x78, 0x18, 0x06), Color.FromRgb(0xEE, 0x55, 0x1C), Color.FromRgb(0xFF, 0xB7, 0x6E)])
    ];

    private readonly Ellipse _dialBackground = new() { Width = Dial, Height = Dial };
    private readonly Ellipse _swatch = new();
    private int _colour;

    private static RadialGradientBrush DialBrush(Color[] stops)
    {
        var brush = new RadialGradientBrush(
        [
            new GradientStop(stops[0], 0),
            new GradientStop(stops[1], 0.55),
            new GradientStop(stops[2], 0.88),
            new GradientStop(stops[3], 1)
        ]);
        brush.Freeze();
        return brush;
    }

    /// <summary>All watch backgrounds: the colours first, then the flags and emblems.</summary>
    private static int DialCount => DialColours.Length + Flags.Length + (HasMyFlag ? 1 : 0);

    /// <summary>The number of "your flag" in the background list (the last one), or -1 when there's none.</summary>
    public static int MyFlagIndex => HasMyFlag ? DialColours.Length + Flags.Length : -1;

    private static string DialName(int index) =>
        index < DialColours.Length ? DialColours[index].Name
        : index - DialColours.Length < Flags.Length ? Flags[index - DialColours.Length]
        : MyFlagName;

    /// <summary>The background for a colour or flag number (null = see-through glass).</summary>
    private static System.Windows.Media.Brush? DialFill(int index) =>
        index < DialColours.Length ? DialColours[index].Stops is { } stops ? DialBrush(stops) : null
        : index - DialColours.Length < Flags.Length ? FlagBrush(Flags[index - DialColours.Length])
        : FlagBrush(MyFlagKey);

    private void StepColour()
    {
        _colour = (_colour + 1) % DialCount;
        _settings.ClockColour = _colour;
        _settings.Save();
        ApplyDial();
        Flash($"{DialName(_colour)} watch · click the swatch or right-click the watch for the next colour or flag");
    }

    /// <summary>Background of the watch: a colour, a flag, or glass. The glow face is purple when glass is chosen.</summary>
    private void ApplyDial()
    {
        // The swatch shows the next colour or flag, so it's clear what a click will do.
        _swatch.Fill = DialFill((_colour + 1) % DialCount) ?? new SolidColorBrush(Theme.InkA(0x60));

        var fill = DialFill(_colour) ?? (_face == WatchFace.Glow ? DialBrush(DialColours[1].Stops!) : null);
        if (fill is not null)
        {
            _dialBackground.Fill = fill;
            _hubGlass.Fill = Brushes.Transparent;
        }
        else
        {
            _dialBackground.Fill = Brushes.Transparent;
            _hubGlass.Fill = new SolidColorBrush(_face == WatchFace.Radio
                ? Color.FromArgb(0x40, 0x1A, 0x10, 0x04)
                : Theme.InkA(0x30));
        }
    }

    private static readonly Geometry PlayGlyph = Geometry.Parse("M0,0 L14,8 L0,16 Z");
    private static readonly Geometry StopGlyph = Geometry.Parse("M0,0 H13 V13 H0 Z");

    private void BuildWatch(WpfPoint c)
    {
        Canvas.SetLeft(_faceHost, c.X - HubGlass);
        Canvas.SetTop(_faceHost, c.Y - HubGlass);
        _faceHost.Clip = new EllipseGeometry(new WpfPoint(HubGlass, HubGlass), HubGlass - 1, HubGlass - 1);
        _faceHost.Children.Add(_dialBackground);
        _colour = Math.Clamp(_settings.ClockColour, 0, DialCount - 1);

        _faces[(int)WatchFace.Digital] = DigitalFace();
        _faces[(int)WatchFace.Classic] = AnalogFace(_classicHour, _classicMinute, _classicSecond, glow: false, numerals: 12);
        _faces[(int)WatchFace.Classic24] = AnalogFace(_dayHour, _dayMinute, _daySecond, glow: false, numerals: 24);
        _faces[(int)WatchFace.Glow] = AnalogFace(_glowHour, _glowMinute, _glowSecond, glow: true, numerals: 0);
        _faces[(int)WatchFace.Calendar] = CalendarFace();
        _faces[(int)WatchFace.Timer] = TimerFace();
        _faces[(int)WatchFace.Radio] = RadioFace();
        _faces[(int)WatchFace.Volume] = VolumeFace();
        _faces[(int)WatchFace.World] = WorldFace();
        _faces[(int)WatchFace.Weather] = WeatherFace();
        foreach (var face in _faces)
        {
            _faceHost.Children.Add(face);
        }

        _ring.Children.Add(_faceHost);

        // Play / stop button for the radio face (outside the face host so it can be clicked).
        _radioButtonFace.Fill = new SolidColorBrush(Color.FromArgb(0xE6, Amber.R, Amber.G, Amber.B));
        _radioButtonFace.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.45, Color = Theme.ShadowColor };
        _radioButton.Children.Add(_radioButtonFace);
        _radioButton.Children.Add(_radioButtonGlyph);
        _radioButton.ToolTip = "Play or stop the radio · scroll for radio volume";
        _radioButton.Tag = "radio button";
        _radioButton.MouseLeftButtonDown += (_, e) => e.Handled = true; // don't start dragging the ring
        _radioButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleRadio(); };
        _radioButton.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            _radio.Volume += e.Delta > 0 ? 5 : -5;
            UpdateFace();
        };
        Canvas.SetLeft(_radioButton, c.X - 20);
        Canvas.SetTop(_radioButton, c.Y + 8);
        _ring.Children.Add(_radioButton);

        // Timer button: start / pause / stop ringing; scroll to set the length.
        _timerButtonFace.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.45, Color = Theme.ShadowColor };
        _timerButton.Children.Add(_timerButtonFace);
        _timerButton.Children.Add(_timerButtonGlyph);
        _timerButton.ToolTip = "Start or pause the timer · scroll to set how long";
        _timerButton.Tag = "timer button";
        _timerButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _timerButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; Flash(_timer.Press()); UpdateFace(); };
        _timerButton.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (_timer.IsRunning)
            {
                Flash("Pause the timer to change its length");
                return;
            }

            _timer.Nudge(e.Delta > 0 ? 1 : -1);
            Flash($"Timer set to {FocusTimer.Describe(_timer.Duration)} · press to start");
        };
        Canvas.SetLeft(_timerButton, c.X - 20);
        Canvas.SetTop(_timerButton, c.Y + 12);
        _ring.Children.Add(_timerButton);
        BuildVolumeButtons(c);

        // Dots to pick a face.
        var dots = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < FaceCount; i++)
        {
            var index = i;
            var dot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(2, 0, 2, 0), Fill = Theme.InkBrush };
            var target = new Grid { Width = 9, Height = 14, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = FaceNames[i] + " face" };
            target.Children.Add(dot);
            target.MouseLeftButtonDown += (_, e) => e.Handled = true;
            target.MouseLeftButtonUp += (_, e) => { e.Handled = true; FlipTo((WatchFace)index); };
            _faceDots[i] = dot;
            dots.Children.Add(target);
        }

        dots.MouseWheel += (_, e) => { StepFace(e.Delta < 0 ? 1 : -1); e.Handled = true; };
        Canvas.SetLeft(dots, c.X - FaceCount * 9 / 2.0);
        Canvas.SetTop(dots, c.Y + HubGlass - 17);
        _ring.Children.Add(dots);

        // ‹ and › on either side of the watch: previous / next watch.
        foreach (var (text, step, side) in new[] { ("‹", -1, -1), ("›", 1, 1) })
        {
            var arrow = new Grid
            {
                Width = 22,
                Height = 34,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = step < 0 ? "Previous watch" : "Next watch",
                Tag = step < 0 ? "previous arrow" : "next arrow"
            };
            arrow.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.InkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 0),
                IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8, Color = Theme.ShadowColor }
            });
            arrow.MouseLeftButtonDown += (_, e) => e.Handled = true;
            arrow.MouseLeftButtonUp += (_, e) => { e.Handled = true; StepFace(step); };
            Canvas.SetLeft(arrow, c.X + side * (HubGlass + 7) - 11);
            Canvas.SetTop(arrow, c.Y - 17);
            _ring.Children.Add(arrow);
        }

        // Colour swatch just outside the watch: click to flip the watch's colour theme (same as right-clicking the watch).
        _swatch.Width = _swatch.Height = 16;
        _swatch.StrokeThickness = 1.5;
        _swatch.Stroke = new SolidColorBrush(Theme.InkA(0xC0));
        _swatch.Cursor = Cursors.Hand;
        _swatch.ToolTip = "Change the watch colour";
        _swatch.Tag = "colour swatch";
        _swatch.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6, Color = Theme.ShadowColor };
        _swatch.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _swatch.MouseLeftButtonUp += (_, e) => { e.Handled = true; StepColour(); };
        var swatchAt = Polar(c, HubDark + 12, 128);
        Canvas.SetLeft(_swatch, swatchAt.X - 8);
        Canvas.SetTop(_swatch, swatchAt.Y - 8);
        _ring.Children.Add(_swatch);

        _hintPill.Child = _hintText;
        _hintHost.Children.Add(_hintPill);
        _root.Children.Add(_hintHost);
        SetHint(string.Empty);

        SetFace((WatchFace)Math.Clamp(_settings.ClockFace, 0, FaceCount - 1), remember: false);
    }

    // ---------------------------------------------------------------- faces

    private UIElement DigitalFace()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        _hubMain.TextTrimming = TextTrimming.CharacterEllipsis;
        _hubSub.Foreground = new SolidColorBrush(Theme.InkA(0xD9));
        stack.Children.Add(_hubMain);
        stack.Children.Add(_hubSub);
        stack.Children.Add(_hubWeather); // e.g. "Malta 22° cloudy"
        return stack;
    }

    /// <summary>An analog dial. numerals: 0 = ticks only, 12 = 1–12, 24 = 1–24 (the hour hand goes round once a day).</summary>
    private static UIElement AnalogFace(RotateTransform hour, RotateTransform minute, RotateTransform second, bool glow, int numerals)
    {
        var canvas = new Canvas { Width = Dial, Height = Dial };
        var c = new WpfPoint(HubGlass, HubGlass);
        var marks = numerals == 24 ? 24 : 12;

        for (var i = 0; i < marks; i++)
        {
            var angle = i * (360.0 / marks) * Math.PI / 180;
            var major = marks == 24 ? i % 6 == 0 : i % 3 == 0;
            double outer = HubGlass - 5, inner = HubGlass - (numerals > 0 ? (major ? 11 : 8) : (major ? 17 : 12));
            canvas.Children.Add(new Line
            {
                X1 = c.X + Math.Sin(angle) * inner,
                Y1 = c.Y - Math.Cos(angle) * inner,
                X2 = c.X + Math.Sin(angle) * outer,
                Y2 = c.Y - Math.Cos(angle) * outer,
                Stroke = new SolidColorBrush(Theme.InkA((byte)(glow ? 0x80 : major ? 0xF0 : 0x90))),
                StrokeThickness = major ? 2.6 : 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        // Numbers inside the ticks: 12 at the top for the 12-hour dial, 24 at the top for the 24-hour dial.
        if (numerals > 0)
        {
            var radius = numerals == 24 ? HubGlass - 19 : HubGlass - 21;
            for (var n = 1; n <= numerals; n++)
            {
                var angle = n * (360.0 / numerals) * Math.PI / 180;
                var even = numerals == 12 || n % 2 == 0;
                var label = new TextBlock
                {
                    Text = n.ToString(),
                    FontSize = numerals == 24 ? (n % 6 == 0 ? 10 : 7.5) : 12,
                    FontWeight = numerals == 24 && n % 6 != 0 ? FontWeights.Normal : FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Theme.InkA((byte)(even ? 0xF0 : 0xA8))),
                    Width = 22,
                    TextAlignment = TextAlignment.Center,
                    Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.7, Color = Theme.ShadowColor }
                };
                var height = label.FontSize * 1.33;
                Canvas.SetLeft(label, c.X + Math.Sin(angle) * radius - 11);
                Canvas.SetTop(label, c.Y - Math.Cos(angle) * radius - height / 2);
                canvas.Children.Add(label);
            }
        }

        // Shorter hands when there are numbers, so the numbers stay readable.
        var hourLength = numerals > 0 ? 27 : 34;
        var minuteLength = numerals > 0 ? 44 : 52;
        canvas.Children.Add(Hand(c, hourLength, 3.6, Theme.InkBrush, hour));
        canvas.Children.Add(Hand(c, minuteLength, 2.4, Theme.InkBrush, minute));
        canvas.Children.Add(Hand(c, numerals > 0 ? 50 : 58, 1.2, new SolidColorBrush(glow ? Colors.White : Amber), second, tail: 12));
        canvas.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Theme.InkBrush, Margin = new Thickness(c.X - 3.5, c.Y - 3.5, 0, 0) });
        foreach (UIElement child in canvas.Children)
        {
            if (child is Line or Ellipse { Width: 7 })
            {
                child.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6, Color = Theme.ShadowColor };
            }
        }

        return canvas;
    }

    private static Line Hand(WpfPoint c, double length, double thickness, System.Windows.Media.Brush brush, RotateTransform rotation, double tail = 6)
    {
        rotation.CenterX = c.X;
        rotation.CenterY = c.Y;
        return new Line
        {
            X1 = c.X,
            Y1 = c.Y + tail,
            X2 = c.X,
            Y2 = c.Y - length,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = rotation
        };
    }

    private UIElement CalendarFace()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        _calDay.Foreground = new SolidColorBrush(Amber);
        _calNumber.Margin = new Thickness(0, -6, 0, -6);
        stack.Children.Add(_calDay);
        stack.Children.Add(_calNumber);
        stack.Children.Add(_calMonth);
        stack.Children.Add(_calWeek);
        return stack;
    }

    private UIElement TimerFace()
    {
        var grid = new Grid();
        grid.Children.Add(_timerArc);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 20, 12, 0) };
        stack.Children.Add(_timerLabel);
        _timerTime.Margin = new Thickness(0, -2, 0, -2);
        stack.Children.Add(_timerTime);
        stack.Children.Add(_timerSub);
        grid.Children.Add(stack);
        return grid;
    }

    private void UpdateTimerFace()
    {
        var ringing = _timer.IsRinging;
        _timerLabel.Text = ringing ? "TIME'S UP" : _timer.IsRunning ? "WORK TIMER" : _timer.IsPaused ? "PAUSED" : "TIMER";
        _timerLabel.Foreground = new SolidColorBrush(ringing ? Amber : Theme.InkA(0xC0));
        _timerTime.Text = ringing ? "0:00" : FocusTimer.Format(_timer.Remaining);
        _timerSub.Text = ringing ? "press to stop ringing"
            : _timer.IsRunning ? $"ends at {_timer.EndsAt:HH:mm}"
            : _timer.IsPaused ? "press to carry on"
            : $"{FocusTimer.Describe(_timer.Duration)} · scroll button to set";

        _timerButtonGlyph.Data = ringing ? StopGlyph : _timer.IsRunning ? PauseGlyph : PlayGlyph;
        _timerButtonGlyph.Margin = ringing ? new Thickness(13.5) : _timer.IsRunning ? new Thickness(13.5, 13, 13.5, 13) : new Thickness(15, 12, 11, 12);
        _timerButtonFace.Fill = new SolidColorBrush(ringing || _timer.IsRunning
            ? Color.FromArgb(0xE6, Amber.R, Amber.G, Amber.B)
            : Color.FromArgb(0xE6, Steel.R, Steel.G, Steel.B));

        // Orange ring around the dial: the time that's left.
        var left = Math.Clamp(1 - _timer.Progress, 0, 1);
        _timerArc.Stroke = new SolidColorBrush(Amber);
        _timerArc.Visibility = left > 0.001 && (_timer.IsRunning || _timer.IsPaused) ? Visibility.Visible : Visibility.Collapsed;
        if (_timerArc.Visibility == Visibility.Visible)
        {
            var c = new WpfPoint(HubGlass, HubGlass);
            var r = HubGlass - 4;
            var sweep = Math.Min(359.9, 360 * left);
            var start = new WpfPoint(c.X, c.Y - r);
            var endAngle = (-90 + sweep) * Math.PI / 180;
            var end = new WpfPoint(c.X + Math.Cos(endAngle) * r, c.Y + Math.Sin(endAngle) * r);
            var figure = new PathFigure { StartPoint = start };
            figure.Segments.Add(new ArcSegment(end, new System.Windows.Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
            _timerArc.Data = new PathGeometry([figure]);
        }
    }

    private UIElement RadioFace()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 24, 14, 0) };
        stack.Children.Add(Label(8.5, FontWeights.SemiBold, 0.7).With("RADIO"));
        _radioName.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(_radioName);
        stack.Children.Add(_radioState);
        return stack;
    }

    // ---------------------------------------------------------------- switching and updating

    private void StepFace(int step) => FlipTo((WatchFace)(((int)_face + step + FaceCount) % FaceCount));

    private readonly ScaleTransform _flip = new(1, 1, HubGlass, HubGlass);
    private bool _flipping;

    /// <summary>Turns the watch over like a card (narrows to an edge, swaps, opens again) and shows the new face.</summary>
    private void FlipTo(WatchFace face)
    {
        if (face == _face || _flipping)
        {
            if (!_flipping)
            {
                SetFace(face, remember: true);
            }

            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            SetFace(face, remember: true);
            return;
        }

        _flipping = true;
        _faceHost.RenderTransform = _flip;
        var close = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };
        close.Completed += (_, _) =>
        {
            SetFace(face, remember: true);
            var open = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            open.Completed += (_, _) => _flipping = false;
            _flip.BeginAnimation(ScaleTransform.ScaleXProperty, open);
        };
        _flip.BeginAnimation(ScaleTransform.ScaleXProperty, close);
    }

    /// <summary>Self-test: flips like a click on the watch would. Returns the face name now showing.</summary>
    /// <summary>Self-test: draws System › Controls and System › Shortcuts (bubbles included) side by side into a PNG.</summary>
    public void RenderSystemPreview(string path) => RenderViewsPreview(path, ("system", "controls"), ("system", "shortcuts"));

    /// <summary>Self-test: draws sections (and their sub-groups) side by side into a PNG, as they look when open.</summary>
    public void RenderViewsPreview(string path, params (string Sector, string? View)[] views)
    {
        var (sector, view, social, query, face) = (_sector, _systemView, _socialView, _query, _face);
        if (!IsVisible)
        {
            // Lay the ring out as if it were open, without showing it.
            _mirrored = false;
            _center = new WpfPoint(RingOffset, CenterY);
            Canvas.SetLeft(_ring, _center.X - Radius);
            Canvas.SetTop(_ring, _center.Y - Radius);
            Canvas.SetLeft(_hintHost, _center.X - _hintHost.Width / 2);
            Canvas.SetTop(_hintHost, _center.Y + Radius + 10);
            Canvas.SetLeft(_searchBox, _center.X - SearchWidth / 2);
            Canvas.SetTop(_searchBox, SearchTop);
            Canvas.SetLeft(_results, _center.X - SearchWidth / 2);
            Canvas.SetTop(_results, SearchTop + SearchHeight + 8);
            PlacePin();
        }

        const int width = 640, height = 640;
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        foreach (var (sectorName, name) in views)
        {
            _sector = Array.FindIndex(Sectors, s => s.Name == sectorName);
            _systemView = sectorName == "system" ? name : null;
            _socialView = sectorName == "web" ? name : null;
            if (sectorName == "volume")
            {
                _sector = Array.FindIndex(Sectors, s => s.Name == "system");
                SetFace(WatchFace.Volume, remember: false);
            }
            _query = string.Empty;
            Refresh();
            foreach (var view2 in _bubbleViews)
            {
                view2.BeginAnimation(OpacityProperty, null);
                view2.Opacity = 1;
                view2.RenderTransform = null;
            }

            _root.Measure(new System.Windows.Size(WindowWidth, WindowHeight));
            _root.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
            _root.UpdateLayout();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x45, 0x55), Color.FromRgb(0x1D, 0x22, 0x2A), 45), null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(_root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, Viewbox = new Rect(0, 0, width, height), ViewboxUnits = BrushMappingMode.Absolute }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            frames.Add(bitmap);
        }

        (_sector, _systemView, _socialView, _query) = (sector, view, social, query);
        SetFace(face, remember: false);
        Refresh();

        var strip = new DrawingVisual();
        using (var context = strip.RenderOpen())
        {
            for (var i = 0; i < frames.Count; i++)
            {
                context.DrawImage(frames[i], new Rect(i * width, 0, width, height));
            }
        }

        var all = new System.Windows.Media.Imaging.RenderTargetBitmap(width * frames.Count, height, 96, 96, PixelFormats.Pbgra32);
        all.Render(strip);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    public void ClickWatchForTest() => StepFace(1);

    public string CurrentWatch => FaceNames[(int)_face];

    /// <summary>Self-test: which element would receive a click at a point given relative to the watch centre.</summary>
    public string HitTestWatch(double dx, double dy)
    {
        _ring.Measure(new System.Windows.Size(Radius * 2, Radius * 2));
        _ring.Arrange(new Rect(0, 0, Radius * 2, Radius * 2));
        _ring.UpdateLayout();
        // Walk the drawn shapes topmost-first, skipping anything that can't take clicks (the same rules a real click follows),
        // so this works while the ring window is hidden.
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(_ring,
            target => target is UIElement { IsHitTestVisible: false } or UIElement { Visibility: not Visibility.Visible }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                if (result.VisualHit is Shape { Fill: null, Stroke: null } or System.Windows.Controls.Panel { Background: null })
                {
                    return HitTestResultBehavior.Continue; // no paint here: a real click passes through
                }

                hit = result.VisualHit;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(new WpfPoint(Radius + dx, Radius + dy)));
        for (var element = hit; element is not null && element != _ring; element = VisualTreeHelper.GetParent(element))
        {
            if (element == _hubGlass)
            {
                return "watch";
            }

            if (element is FrameworkElement { Tag: string name })
            {
                return name;
            }
        }

        return hit?.GetType().Name ?? "nothing";
    }

    private void SetFace(WatchFace face, bool remember)
    {
        _face = face;
        if (remember)
        {
            _settings.ClockFace = (int)face;
            _settings.Save();
            Flash($"{FaceNames[(int)face]} watch · scroll over the watch to change");
        }

        for (var i = 0; i < FaceCount; i++)
        {
            _faces[i].Visibility = i == (int)face ? Visibility.Visible : Visibility.Collapsed;
            _faceDots[i].Opacity = i == (int)face ? 1 : 0.35;
        }

        _radioButton.Visibility = face == WatchFace.Radio ? Visibility.Visible : Visibility.Collapsed;
        _timerButton.Visibility = face == WatchFace.Timer ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _volumeButtons)
        {
            button.Visibility = face == WatchFace.Volume ? Visibility.Visible : Visibility.Collapsed;
        }
        ApplyDial();
        UpdateSkyRunning();
        UpdateFace();
    }

    private void UpdateFace()
    {
        var now = DateTime.Now;
        switch (_face)
        {
            case WatchFace.Digital:
                _hubMain.Text = now.ToString("HH:mm:ss");
                _hubSub.Text = _query.Length > 0
                    ? (_items.Count == 0 ? "no matches" : "Enter to open")
                    : now.ToString("ddd dd/MM/yyyy");
                _hubWeather.Text = HomePlace is { } home && _world.WeatherFor(home) is { } today
                    ? $"{home.Name} {Math.Round(today.Temperature)}° {today.Conditions}"
                    : string.Empty;
                break;

            case WatchFace.Classic24:
                _dayHour.Angle = (now.Hour + now.Minute / 60.0) * 15; // once round per day, 24 at the top
                _dayMinute.Angle = (now.Minute + now.Second / 60.0) * 6;
                _daySecond.Angle = now.Second * 6;
                break;

            case WatchFace.Classic:
            case WatchFace.Glow:
                var (hour, minute, second) = _face == WatchFace.Classic
                    ? (_classicHour, _classicMinute, _classicSecond)
                    : (_glowHour, _glowMinute, _glowSecond);
                hour.Angle = (now.Hour % 12 + now.Minute / 60.0) * 30;
                minute.Angle = (now.Minute + now.Second / 60.0) * 6;
                second.Angle = now.Second * 6;
                break;

            case WatchFace.Weather:
                UpdateWeatherFace();
                break;

            case WatchFace.World:
                UpdateWorldFace();
                break;

            case WatchFace.Volume:
                UpdateVolumeFace();
                break;

            case WatchFace.Timer:
                UpdateTimerFace();
                break;

            case WatchFace.Calendar:
                _calDay.Text = now.ToString("dddd").ToUpperInvariant();
                _calNumber.Text = now.Day.ToString();
                _calMonth.Text = now.ToString("MMMM yyyy");
                BuildWeek(now);
                break;

            case WatchFace.Radio:
                var station = _radio.Current ?? LastStation();
                _radioName.Text = station?.DisplayName ?? "No station yet";
                _radioState.Text = _radio.State switch
                {
                    RadioState.Playing => $"playing · {_radio.Volume}%",
                    RadioState.Connecting => "connecting…",
                    _ => station is null ? "pick one in Radio" : "stopped · press play"
                };
                var playing = _radio.State != RadioState.Stopped;
                _radioButtonGlyph.Data = playing ? StopGlyph : PlayGlyph;
                _radioButtonGlyph.Margin = playing ? new Thickness(13.5) : new Thickness(15, 12, 11, 12);
                _radioButtonFace.Fill = new SolidColorBrush(playing
                    ? Color.FromArgb(0xE6, Amber.R, Amber.G, Amber.B)
                    : Color.FromArgb(0xE6, Steel.R, Steel.G, Steel.B));
                break;
        }
    }

    private DateTime _weekBuiltFor;

    private void BuildWeek(DateTime today)
    {
        if (_weekBuiltFor == today.Date)
        {
            return;
        }

        _weekBuiltFor = today.Date;
        _calWeek.Children.Clear();
        var monday = today.Date.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        for (var i = 0; i < 7; i++)
        {
            var day = monday.AddDays(i);
            var isToday = day == today.Date;
            var cell = new Grid { Width = 15, Height = 15 };
            if (isToday)
            {
                cell.Children.Add(new Ellipse { Fill = new SolidColorBrush(Amber) });
            }

            cell.Children.Add(new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 8,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new SolidColorBrush(Theme.InkA((byte)(isToday ? 0xFF : 0xB0))),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            _calWeek.Children.Add(cell);
        }
    }

    private Station? LastStation() =>
        _settings.RadioRecent.FirstOrDefault() ?? _settings.RadioFavorites.FirstOrDefault() ?? _radio.CachedTop("MT")?.FirstOrDefault();

    private void ToggleRadio()
    {
        if (_radio.State != RadioState.Stopped)
        {
            _radio.Stop();
            Flash("Radio stopped");
        }
        else if (LastStation() is { } station)
        {
            _radio.Play(station);
            Flash($"Playing {station.DisplayName}");
        }
        else
        {
            Flash("Pick a station in the Radio section first");
        }

        UpdateFace();
        if (Sectors[_sector].Name == "radio" && _query.Length == 0)
        {
            Refresh();
        }
    }

    /// <summary>When a station starts, show the radio face so the play / stop button is right there.</summary>
    private void OnRadioStateForWatch()
    {
        if (_radio.State == RadioState.Connecting && _face != WatchFace.Radio)
        {
            SetFace(WatchFace.Radio, remember: false);
        }
        else
        {
            UpdateFace();
        }
    }

    // ---------------------------------------------------------------- self-test support

    /// <summary>Draws the ring's centre with each watch face, side by side, into a PNG (without showing the window).</summary>
    public void RenderWatchPreview(string path, bool includeLights)
    {
        var original = _face;
        var originalColour = _colour;
        const int size = 420; // ring is 380 across; small margin
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();

        // Row 1: every face in glass. Row 2: the four colours on different faces.
        var shots = Enum.GetValues<WatchFace>().Select(face => (Face: face, Colour: 0)).ToList();
        shots.AddRange([(WatchFace.Classic, 1), (WatchFace.Classic24, 2), (WatchFace.Calendar, 3), (WatchFace.Radio, 4), (WatchFace.Digital, 1), (WatchFace.Classic, 4)]);
        foreach (var (face, colour) in shots)
        {
            _colour = colour;
            SetFace(face, remember: false);
            if (includeLights && face == WatchFace.Radio)
            {
                _lights.Visibility = Visibility.Visible;
                for (var i = 0; i < 6; i++)
                {
                    AnimateLights();
                }
            }

            _ring.Measure(new System.Windows.Size(Radius * 2, Radius * 2));
            _ring.Arrange(new Rect(0, 0, Radius * 2, Radius * 2));
            _ring.UpdateLayout();

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // A blurred-desktop-like backdrop so the glass reads as it does on screen.
                context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x45, 0x55), Color.FromRgb(0x1D, 0x22, 0x2A), 45), null, new Rect(0, 0, size, size));
                context.DrawRectangle(new VisualBrush(_ring) { Stretch = Stretch.None }, null, new Rect((size - Radius * 2) / 2, (size - Radius * 2) / 2, Radius * 2, Radius * 2));
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            frames.Add(bitmap);
        }

        if (!IsVisible || _radio.State != RadioState.Playing)
        {
            _lights.Visibility = Visibility.Collapsed;
        }

        _colour = originalColour;
        SetFace(original, remember: false);

        const int columns = FaceCount;
        var rows = (frames.Count + columns - 1) / columns;
        var strip = new DrawingVisual();
        using (var context = strip.RenderOpen())
        {
            for (var i = 0; i < frames.Count; i++)
            {
                context.DrawImage(frames[i], new Rect(i % columns * size, i / columns * size, size, size));
            }
        }

        var all = new System.Windows.Media.Imaging.RenderTargetBitmap(size * columns, size * rows, 96, 96, PixelFormats.Pbgra32);
        all.Render(strip);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Presses the radio face's play / stop button (self-test). Returns the face showing afterwards.</summary>
    public string PressRadioButton()
    {
        ToggleRadio();
        return FaceNames[(int)_face];
    }

    public bool HasStationToResume => LastStation() is not null;

    /// <summary>Called when the work timer runs out: open the ring on the timer face.</summary>
    public void ShowTimeUp()
    {
        ShowRing(null);
        SetFace(WatchFace.Timer, remember: false);
        Flash("Time's up! Press the button on the watch to stop the ringing");
    }

    /// <summary>Items for "t 2h", "t 45m", "t 1h30", "t stop".</summary>
    private List<RingItem> TimerSearch(string text)
    {
        var items = new List<RingItem>();
        if (_timer.IsRunning || _timer.IsPaused || _timer.IsRinging)
        {
            items.Add(new RingItem
            {
                Title = _timer.IsRinging ? "Stop the ringing" : $"Stop the timer ({FocusTimer.Format(_timer.Remaining)} left)",
                Glyph = Glyphs.Stop,
                Subtitle = "Cancels the work timer",
                ClosesRing = false,
                Activate = () => { _timer.Cancel(); SetQuery(string.Empty); SetFace(WatchFace.Timer, remember: false); Flash("Timer cancelled"); }
            });
        }

        if (ParseDuration(text) is { } duration)
        {
            items.Insert(0, new RingItem
            {
                Title = $"Start a {FocusTimer.Describe(duration)} timer",
                Glyph = Glyphs.Timer,
                Subtitle = $"Rings at {DateTime.Now + duration:HH:mm}",
                ClosesRing = false,
                Activate = () =>
                {
                    _timer.Start(duration);
                    SetQuery(string.Empty);
                    SetFace(WatchFace.Timer, remember: false);
                    Flash($"Timer started: rings at {_timer.EndsAt:HH:mm}");
                }
            });
        }
        else if (items.Count == 0)
        {
            items.Add(Info("Timer", Glyphs.Timer, "Type how long, e.g. t 2h, t 45m, t 1h30"));
        }

        return items;
    }

    /// <summary>"2h", "2 hours", "45m", "45", "1h30", "1h 30m", "1:30", "90 min".</summary>
    internal static TimeSpan? ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant();
        var clock = System.Text.RegularExpressions.Regex.Match(text, @"^(\d{1,2}):(\d{2})$");
        if (clock.Success)
        {
            return Positive(TimeSpan.FromHours(int.Parse(clock.Groups[1].Value)) + TimeSpan.FromMinutes(int.Parse(clock.Groups[2].Value)));
        }

        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"^(?:(\d+(?:[.,]\d+)?)\s*(?:h|hr|hrs|hour|hours))?\s*(?:(\d+)\s*(?:m|min|mins|minute|minutes)?)?$");
        if (!match.Success || (match.Groups[1].Length == 0 && match.Groups[2].Length == 0))
        {
            return null;
        }

        var hours = match.Groups[1].Length > 0 ? double.Parse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture) : 0;
        var minutes = match.Groups[2].Length > 0 ? int.Parse(match.Groups[2].Value) : 0;
        return Positive(TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes));

        static TimeSpan? Positive(TimeSpan span) => span > TimeSpan.Zero && span <= TimeSpan.FromHours(24) ? span : null;
    }

    private void SetHint(string text)
    {
        _hintText.Text = text;
        _hintPill.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock Label(double size, FontWeight weight, double opacity) => new()
    {
        FontSize = size,
        FontWeight = weight,
        Foreground = new SolidColorBrush(Theme.InkA((byte)(opacity * 255))),
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.75, Color = Theme.ShadowColor }
    };
}

internal static class TextBlockExtensions
{
    public static TextBlock With(this TextBlock block, string text)
    {
        block.Text = text;
        return block;
    }
}
