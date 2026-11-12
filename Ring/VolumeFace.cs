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
using WpfPoint = System.Windows.Point;

namespace DesktopOrbit.Ring;

/// <summary>
/// The Volume watch: a segmented level ring (open at the bottom), the volume in big numbers,
/// a speaker button to mute, and − / + buttons. Scrolling over this watch changes the volume.
/// </summary>
public sealed partial class RingWindow
{
    private const int VolumeSegments = 28;
    private const double VolumeSweep = 270;      // degrees, open at the bottom for the buttons
    private const double VolumeStart = 135;      // bottom-left, going clockwise over the top
    private const int VolumeStep = 4;            // % per click of − / +

    private readonly Path[] _volumeSegments = new Path[VolumeSegments];
    private readonly TextBlock _volumePercent = Label(28, FontWeights.SemiBold, 1);
    private readonly TextBlock _volumeLabel = Label(8.5, FontWeights.SemiBold, 0.7);
    private readonly List<FrameworkElement> _volumeButtons = [];
    private Path _volumeSpeaker = null!;

    private static readonly SolidColorBrush SegmentOff = Frozen(Theme.InkA(0x38));
    private static readonly SolidColorBrush SegmentMuted = Frozen(Color.FromArgb(0x70, 0x9A, 0xA1, 0xAD));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Segment colour: warm amber at the low end, hot orange-red at the top.</summary>
    private static SolidColorBrush SegmentOn(double t) => Frozen(Color.FromRgb(
        (byte)(0xF5 + (0xFF - 0xF5) * t),
        (byte)(0xB0 - (0xB0 - 0x55) * t),
        (byte)(0x3A - (0x3A - 0x1C) * t)));

    private UIElement VolumeFace()
    {
        var grid = new Grid();
        var canvas = new Canvas { Width = Dial, Height = Dial };
        var c = new WpfPoint(HubGlass, HubGlass);
        const double radius = HubGlass - 12;
        var each = VolumeSweep / VolumeSegments;
        for (var i = 0; i < VolumeSegments; i++)
        {
            var from = VolumeStart + i * each + 1.4;
            var to = VolumeStart + (i + 1) * each - 1.4;
            var figure = new PathFigure { StartPoint = Polar(c, radius, from) };
            figure.Segments.Add(new ArcSegment(Polar(c, radius, to), new System.Windows.Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
            var segment = new Path
            {
                Data = new PathGeometry([figure]),
                StrokeThickness = 8,
                Stroke = SegmentOff,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat
            };
            _volumeSegments[i] = segment;
            canvas.Children.Add(segment);
        }

        grid.Children.Add(canvas);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 22) };
        _volumePercent.Margin = new Thickness(0, -2, 0, -4);
        stack.Children.Add(_volumePercent);
        stack.Children.Add(_volumeLabel);
        grid.Children.Add(stack);
        return grid;
    }

    private void BuildVolumeButtons(WpfPoint c)
    {
        // Speaker at the top of the dial: click to mute or unmute.
        _volumeSpeaker = GlyphPath(Glyphs.Speaker);
        var speaker = RoundButton(30, null, _volumeSpeaker, "Mute or unmute", "volume mute", () =>
        {
            AudioControl.Speakers.ToggleMute();
            Flash(AudioControl.Speakers.IsMuted ? "Sound muted" : $"Sound on · {AudioControl.Speakers.Level}%");
        }, transparent: true);
        Canvas.SetLeft(speaker, c.X - 15);
        Canvas.SetTop(speaker, c.Y - 48);

        var minus = RoundButton(30, "−", null, $"Volume down {VolumeStep}%", "volume down", () => ChangeVolume(-VolumeStep));
        Canvas.SetLeft(minus, c.X - 36);
        Canvas.SetTop(minus, c.Y + 25);

        var plus = RoundButton(30, "+", null, $"Volume up {VolumeStep}%", "volume up", () => ChangeVolume(VolumeStep));
        Canvas.SetLeft(plus, c.X + 6);
        Canvas.SetTop(plus, c.Y + 25);

        foreach (var button in new[] { speaker, minus, plus })
        {
            button.MouseWheel += (_, e) => { e.Handled = true; ChangeVolume(e.Delta > 0 ? 2 : -2); };
            _volumeButtons.Add(button);
            _ring.Children.Add(button);
        }
    }

    private Grid RoundButton(double size, string? text, Path? glyph, string tooltip, string tag, Action click, bool transparent = false)
    {
        var button = new Grid { Width = size, Height = size, Cursor = Cursors.Hand, ToolTip = tooltip, Tag = tag, Background = Brushes.Transparent };
        if (!transparent)
        {
            button.Children.Add(new Ellipse
            {
                Fill = new SolidColorBrush(Color.FromArgb(0xE6, Amber.R, Amber.G, Amber.B)),
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.45, Color = Theme.ShadowColor }
            });
        }

        if (text is not null)
        {
            button.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.InkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -3, 0, 0),
                IsHitTestVisible = false
            });
        }

        if (glyph is not null)
        {
            glyph.IsHitTestVisible = false;
            button.Children.Add(glyph);
        }

        button.MouseLeftButtonDown += (_, e) => e.Handled = true; // don't start dragging the ring
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); UpdateFace(); };
        return button;
    }

    private void ChangeVolume(int percent)
    {
        AudioControl.Speakers.Change(percent);
        UpdateFace();
        RefreshLiveBadges();
    }

    private void UpdateVolumeFace()
    {
        var level = AudioControl.Speakers.Level;
        var muted = AudioControl.Speakers.IsMuted;
        _volumePercent.Text = level is null ? "–" : $"{level}%";
        _volumeLabel.Text = level is null ? "NO SPEAKERS" : muted ? "MUTED" : "VOLUME";
        _volumeLabel.Foreground = new SolidColorBrush(muted ? Amber : Theme.InkA(0xB3));
        _volumeSpeaker.Opacity = muted ? 0.45 : 1;

        var lit = (int)Math.Round((level ?? 0) / 100.0 * VolumeSegments);
        for (var i = 0; i < VolumeSegments; i++)
        {
            _volumeSegments[i].Stroke = i >= lit ? SegmentOff
                : muted ? SegmentMuted
                : SegmentOn((double)i / (VolumeSegments - 1));
        }
    }

    /// <summary>Scrolling over the Volume watch changes the volume; over any other watch it flips to the next one.</summary>
    private void OnWatchWheel(int delta)
    {
        if (_face == WatchFace.Volume)
        {
            ChangeVolume(delta > 0 ? 2 : -2);
        }
        else
        {
            StepFace(delta < 0 ? 1 : -1);
        }
    }

    /// <summary>Self-test: titles shown in the Social section, in order.</summary>
    public IReadOnlyList<string> SocialTitlesForTest()
    {
        var view = _socialView;
        _socialView = null;
        var titles = SocialItems().Select(item => item.Title).ToList();
        _socialView = view;
        return titles;
    }

    /// <summary>Self-test: switches to the Volume watch (or back) without animation.</summary>
    public void ShowVolumeWatchForTest(bool show) => SetFace(show ? WatchFace.Volume : (WatchFace)Math.Clamp(_settings.ClockFace, 0, FaceCount - 1), remember: false);

    /// <summary>Self-test: presses the Volume watch's + and − buttons. Returns the levels seen (before, after +, after −).</summary>
    public (int? Before, int? Up, int? Down) PressVolumeButtonsForTest()
    {
        var before = AudioControl.Speakers.Level;
        ChangeVolume(before <= 100 - VolumeStep ? VolumeStep : -VolumeStep);
        var up = AudioControl.Speakers.Level;
        ChangeVolume(before <= 100 - VolumeStep ? -VolumeStep : VolumeStep);
        return (before, up, AudioControl.Speakers.Level);
    }
}
