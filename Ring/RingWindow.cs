using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopOrbit.Services;
using DesktopOrbit.ShellIntegration;
using Drawing = System.Drawing;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using DriveInfo = System.IO.DriveInfo;
using DriveType = System.IO.DriveType;
using WpfImage = System.Windows.Controls.Image;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace DesktopOrbit.Ring;

/// <summary>
/// The v2 launcher: a frosted-glass ring that opens at the mouse. Six sectors around a hub with the clock;
/// the chosen sector's items fan out to the side as glass bubbles. Typing searches.
/// </summary>
public sealed partial class RingWindow : Window
{
    // Layout, in device-independent pixels.
    private const double WindowWidth = 650;
    private const double WindowHeight = 960;
    private const double RingOffset = 300;   // ring centre from the window's near edge
    private const double CenterY = 320;      // ring centre from the window's top
    private const double Radius = 190;
    private const double HubDark = 88;
    private const double HubGlass = 75;
    private const double BubbleRadius = 41;
    private const double BubbleOrbit = 275;
    private const double PinRadius = 16;

    // The search box sits under the ring; results list below it.
    private const double SearchWidth = 440;
    private const double SearchHeight = 46;
    private const double SearchTop = CenterY + 236;
    private const double RowHeight = 48;
    private const int MaxRows = 7;

    private static Color Steel => Theme.ActiveColor;   // the section you're in (from the theme)
    private static Color Amber => Theme.AccentColor;   // highlights (from the theme)

    private readonly Canvas _root = new();
    private readonly WpfImage _glass = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Width = WindowWidth, Height = WindowHeight };
    private readonly Canvas _ring = new() { Width = Radius * 2, Height = Radius * 2 };
    private readonly Canvas _bubbles = new() { Width = WindowWidth, Height = WindowHeight };
    private readonly Path[] _sectorShapes = new Path[6];
    private readonly Path[] _sectorIcons = new Path[6];
    private readonly TextBlock _hubMain = HubText(26, FontWeights.SemiBold);
    private readonly TextBlock _hubSub = HubText(11, FontWeights.Normal);
    private readonly Path _cpuArc = Arc(Steel);
    private readonly Path _ramArc = Arc(Color.FromRgb(0x9A, 0x7F, 0xD1));
    private readonly Path _diskArc = Arc(Amber);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SystemTelemetryService _telemetry = new();
    private readonly AppCatalog _catalog;
    private readonly RingSettings _settings;
    private readonly RadioService _radio;
    private readonly FocusTimer _timer;
    private readonly Grid _pin = new();
    private readonly Border _searchBox = new();
    private readonly TextBlock _searchText = new();
    private readonly System.Windows.Shapes.Rectangle _searchCaret = new();
    private readonly Border _results = new();
    private readonly StackPanel _resultRows = new();
    private string? _systemView; // null = System home, else "power", "controls" or "shortcuts"
    private double _scale = NativeMethods.GetDpiForSystem() / 96.0;
    private NativeMethods.POINT _dragStart;
    private WpfPoint _dragOrigin;
    private bool _dragging;
    private DateTime _lastGlass;
    private CancellationTokenSource? _radioSearch;
    private string? _radioCountry; // null = radio home, "" = world, else a country code
    private string? _armed;        // a shut down / restart waiting for its second click
    private DateTime _armedUntil;
    private List<RingItem> _radioResults = [];
    private string _radioResultsFor = string.Empty;
    private readonly Action<string> _notify;
    private readonly List<FrameworkElement> _bubbleViews = [];
    private readonly List<(RingItem Item, TextBlock Badge)> _liveBadges = [];

    private WpfPoint _center;
    private bool _mirrored;
    private int _sector;
    private int _hoverSector = -1;
    private int _selected;
    private string _query = string.Empty;
    private List<RingItem> _items = [];
    private string? _flash;
    private DateTime _flashUntil;

    private static readonly (string Name, string Label, string Glyph)[] Sectors =
    [
        ("apps", "APPS", Glyphs.Apps),
        ("web", "SOCIAL", Glyphs.Web),
        ("files", "FILES", Glyphs.Files),
        ("radio", "RADIO", Glyphs.Radio),
        ("ask", "ASK", Glyphs.Mic),
        ("system", "SYSTEM", Glyphs.System)
    ];

    public RingWindow(AppCatalog catalog, RingSettings settings, RadioService radio, FocusTimer timer, Action<string> notify)
    {
        _catalog = catalog;
        _settings = settings;
        _radio = radio;
        _timer = timer;
        _notify = notify;
        _sector = Math.Clamp(settings.LastSector, 0, Sectors.Length - 1);

        Title = "Titanium Orbital";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = WindowWidth;
        Height = WindowHeight;
        FontFamily = new FontFamily(Theme.FontFamily);
        Focusable = true;
        Content = _root;

        RenderOptions.SetBitmapScalingMode(_glass, BitmapScalingMode.HighQuality);
        _glass.Opacity = Theme.Current.Glass; // how much frosted desktop shows through
        _root.Children.Add(_glass);
        _root.Children.Add(_ring);
        _root.Children.Add(_bubbles);
        BuildRing();

        BuildPin();
        BuildSearchBox();
        SetUpDropAndPaste();
        Activated += (_, _) => UpdateHub();

        _clock.Tick += (_, _) =>
        {
            UpdateHub();
            if (!IsActive)
            {
                NoteWindowInFront(); // pinned ring: follow which app you're using
            }

            if (DateTime.Now.Second % 2 == 0)
            {
                RefreshOpenWindows(); // keep the green "open" dots current
                RefreshLiveBadges(); // times on the World bubbles
            }

            if (_settings.Pinned && !_dragging)
            {
                RecaptureGlass(); // windows behind a pinned ring change; keep the glass current
            }
        };
        PreviewKeyDown += OnKey;
        TextInput += OnText;
        Deactivated += (_, _) =>
        {
            if (_settings.Pinned)
            {
                return;
            }

            // Pressing on a desktop shortcut to drag it here: stay open until the mouse is let go (it may be dropped on us).
            if (DropPinsHelpers.LeftButtonDown())
            {
                WaitForDragToEnd();
                return;
            }

            HideRing();
        };
        _world.Updated += () => Dispatcher.BeginInvoke(() => { UpdateFace(); RefreshLiveBadges(); });
        BrightnessControl.Changed += () => Dispatcher.BeginInvoke(RefreshLiveBadges);
        BrightnessControl.Warm();
        Connectivity.Changed += () => Dispatcher.BeginInvoke(RefreshLiveBadges);
        Connectivity.Refresh();
        IsVisibleChanged += (_, _) => { UpdateLightsRunning(); UpdateSkyRunning(); };
        _timer.Changed += () =>
        {
            if (IsVisible && _face == WatchFace.Timer)
            {
                UpdateFace();
            }
        };
        _radio.StateChanged += message =>
        {
            RefreshLiveBadges();
            UpdateLightsRunning();
            OnRadioStateForWatch();
            if (message is null)
            {
                return;
            }

            if (IsVisible)
            {
                Flash(message);
                if (Sectors[_sector].Name == "radio" && _query.Length == 0)
                {
                    Refresh();
                }
            }
            else
            {
                _notify(message);
            }
        };
        _catalog.Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            EnsureDefaultFavorites();
            if (IsVisible)
            {
                Refresh();
            }
        });
    }

    public static int SectorIndex(string? name) =>
        Array.FindIndex(Sectors, sector => string.Equals(sector.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- showing and hiding

    public void Toggle()
    {
        if (IsVisible)
        {
            HideRing();
        }
        else
        {
            ShowRing(null);
        }
    }

    public void ShowRing(string? section)
    {
        var requested = SectorIndex(section);
        if (requested >= 0)
        {
            _sector = requested;
        }

        if (!IsActive)
        {
            NoteWindowInFront(); // the app you were using, so clicking its bubble again closes it
        }

        if (_settings.Pinned && _settings.PinnedLeft is { } pinnedLeft && _settings.PinnedTop is { } pinnedTop)
        {
            _scale = ScaleAt(new NativeMethods.POINT { X = (int)(pinnedLeft * _scale), Y = (int)(pinnedTop * _scale) }, out _);
            _mirrored = _settings.PinnedMirrored;
            PlaceAt(pinnedLeft, pinnedTop);
        }
        else
        {
            PlaceAtCursor();
        }

        _query = string.Empty;
        _selected = 0;
        _flash = null;
        _radioCountry = null;
        _systemView = null;
        _socialView = null;
        _appsPage = 0;
        Refresh();

        if (!IsVisible)
        {
            Show();
            Animate(_root, OpacityProperty, 0, 1, 130);
            var scale = new ScaleTransform(0.94, 0.94, _center.X, _center.Y);
            _root.RenderTransform = scale;
            Animate(scale, ScaleTransform.ScaleXProperty, 0.94, 1, 160);
            Animate(scale, ScaleTransform.ScaleYProperty, 0.94, 1, 160);
        }

        Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Focus();
        _clock.Start();
        RefreshOpenWindows();
    }

    public void HideRing()
    {
        if (!IsVisible)
        {
            return;
        }

        _clock.Stop();
        _settings.LastSector = _sector;
        _settings.Save();
        Hide();
    }

    /// <summary>Display scaling of the monitor containing the point, plus that monitor's info.</summary>
    private double ScaleAt(NativeMethods.POINT point, out NativeMethods.MONITORINFO info)
    {
        var monitor = NativeMethods.MonitorFromPoint(point, 2 /* nearest */);
        info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        NativeMethods.GetDpiForMonitor(monitor, 0, out var monitorDpi, out _);
        if (monitorDpi > 0)
        {
            return monitorDpi / 96.0;
        }

        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle());
        return source?.CompositionTarget is { } target ? target.TransformToDevice.M11 : NativeMethods.GetDpiForSystem() / 96.0;
    }

    /// <summary>After opening something: an unpinned ring closes, a pinned one stays and clears the search.</summary>
    private void CloseAfterLaunch()
    {
        if (!_settings.Pinned)
        {
            HideRing();
        }
        else if (_query.Length > 0)
        {
            SetQuery(string.Empty);
        }
    }

    private void PlaceAtCursor()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var scale = _scale = ScaleAt(cursor, out var info);
        double cx = cursor.X / scale, cy = cursor.Y / scale;
        double workLeft = info.rcWork.Left / scale, workTop = info.rcWork.Top / scale;
        double workRight = info.rcWork.Right / scale, workBottom = info.rcWork.Bottom / scale;

        // Bubbles fan out to the right, unless there is no room there.
        _mirrored = cx + (WindowWidth - RingOffset) > workRight;
        var offsetX = _mirrored ? WindowWidth - RingOffset : RingOffset;
        var left = Math.Clamp(cx - offsetX, workLeft, Math.Max(workLeft, workRight - WindowWidth));
        var top = Math.Clamp(cy - CenterY, workTop, Math.Max(workTop, workBottom - WindowHeight));
        PlaceAt(left, top);
    }

    private void PlaceAt(double left, double top)
    {
        Left = left;
        Top = top;

        var offsetX = _mirrored ? WindowWidth - RingOffset : RingOffset;
        _center = new WpfPoint(offsetX, CenterY);
        Canvas.SetLeft(_ring, _center.X - Radius);
        Canvas.SetTop(_ring, _center.Y - Radius);
        Canvas.SetLeft(_hintHost, _center.X - _hintHost.Width / 2);
        Canvas.SetTop(_hintHost, _center.Y + Radius + 10);
        Canvas.SetLeft(_searchBox, _center.X - SearchWidth / 2);
        Canvas.SetTop(_searchBox, SearchTop);
        Canvas.SetLeft(_results, _center.X - SearchWidth / 2);
        Canvas.SetTop(_results, SearchTop + SearchHeight + 8);

        // The pin sits on the rim, on the side away from the bubbles.
        var pinAt = Polar(_center, Radius - 10, _mirrored ? -45 : -135);
        Canvas.SetLeft(_pin, pinAt.X - PinRadius);
        Canvas.SetTop(_pin, pinAt.Y - PinRadius);
        PaintPin();
        RecaptureGlass();
    }

    private void RecaptureGlass()
    {
        // Hide the ring from screen capture only for this instant, so it reads what's behind itself
        // but still shows up in the user's own screenshots and recordings.
        var handle = new WindowInteropHelper(this).Handle;
        var excluded = IsVisible && handle != IntPtr.Zero && NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        try
        {
            _glass.Source = CaptureFrosted(
                (int)Math.Round(Left * _scale), (int)Math.Round(Top * _scale),
                (int)Math.Round(WindowWidth * _scale), (int)Math.Round(WindowHeight * _scale));
        }
        finally
        {
            if (excluded)
            {
                NativeMethods.SetWindowDisplayAffinity(handle, 0 /* WDA_NONE */);
            }
        }
    }

    // ---------------------------------------------------------------- dragging and pinning

    private void StartDrag(MouseButtonEventArgs e, UIElement handle)
    {
        NativeMethods.GetCursorPos(out _dragStart);
        _dragOrigin = new WpfPoint(Left, Top);
        _dragging = false;
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueDrag(UIElement handle)
    {
        if (!handle.IsMouseCaptured)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var now);
        double dx = (now.X - _dragStart.X) / _scale, dy = (now.Y - _dragStart.Y) / _scale;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 5)
        {
            return;
        }

        _dragging = true;
        Left = _dragOrigin.X + dx;
        Top = _dragOrigin.Y + dy;

        // Keep the glass showing what's behind the new spot, without re-blurring on every mouse move.
        if (DateTime.Now - _lastGlass > TimeSpan.FromMilliseconds(50))
        {
            _lastGlass = DateTime.Now;
            RecaptureGlass();
        }
    }

    /// <summary>Returns true when the press was a drag (so it shouldn't count as a click).</summary>
    private bool EndDrag(UIElement handle)
    {
        if (!handle.IsMouseCaptured)
        {
            return false;
        }

        handle.ReleaseMouseCapture();
        if (!_dragging)
        {
            return false;
        }

        _dragging = false;
        RecaptureGlass();
        if (_settings.Pinned)
        {
            RememberPinnedPlace();
        }

        Flash(_settings.Pinned ? "Moved. The ring stays pinned here" : "Moved. Click the pin to keep it here");
        return true;
    }

    public bool IsPinned => _settings.Pinned;

    public void SetPinned(bool pinned)
    {
        _settings.Pinned = pinned;
        if (pinned)
        {
            RememberPinnedPlace();
        }

        _settings.Save();
        PaintPin();
        PinnedChanged?.Invoke(pinned);
    }

    public event Action<bool>? PinnedChanged;

    private void RememberPinnedPlace()
    {
        _settings.PinnedLeft = Left;
        _settings.PinnedTop = Top;
        _settings.PinnedMirrored = _mirrored;
        _settings.Save();
    }

    private void PaintPin()
    {
        if (_pin.Children.Count < 2)
        {
            return;
        }

        var pinned = _settings.Pinned;
        ((Ellipse)_pin.Children[0]).Fill = new SolidColorBrush(pinned ? Color.FromArgb(0xE6, Amber.R, Amber.G, Amber.B) : Theme.BaseA(0xB8));
        _pin.Children[1].RenderTransform = new RotateTransform(pinned ? 0 : 35, 12, 12);
        _pin.ToolTip = pinned ? "Unpin: the ring opens at the mouse again" : "Pin the ring here: it stays open where you put it";
    }

    private void BuildPin()
    {
        _pin.Width = _pin.Height = PinRadius * 2;
        _pin.Cursor = Cursors.Hand;
        _pin.Children.Add(new Ellipse
        {
            Stroke = new SolidColorBrush(Theme.InkA(0x66)),
            StrokeThickness = Theme.Line(1),
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.4, Color = Theme.ShadowColor }
        });
        var glyph = GlyphPath(Glyphs.Pin);
        glyph.Width = glyph.Height = 24;
        glyph.RenderTransform = new ScaleTransform(0.75, 0.75, 12, 12);
        var holder = new Grid { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        holder.Children.Add(glyph);
        _pin.Children.Add(holder);
        _pin.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SetPinned(!_settings.Pinned);
            Flash(_settings.Pinned ? "Pinned. The ring stays open here. Drag the centre to move it" : "Unpinned. The ring opens at the mouse again");
        };
        _root.Children.Add(_pin);
        PaintPin();
    }

    /// <summary>Snapshot of the screen behind the window, shrunk and blurred: the frosted glass.</summary>
    private static BitmapSource? CaptureFrosted(int x, int y, int width, int height)
    {
        try
        {
            using var shot = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Drawing.Graphics.FromImage(shot))
            {
                g.CopyFromScreen(x, y, 0, 0, new Drawing.Size(width, height));
            }

            int smallWidth = Math.Max(16, width / 6), smallHeight = Math.Max(16, height / 6);
            using var small = new Drawing.Bitmap(smallWidth, smallHeight, Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(shot, 0, 0, smallWidth, smallHeight);
            }

            var handle = small.GetHbitmap();
            BitmapSource source;
            try
            {
                source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                NativeMethods.DeleteObject(handle);
            }

            // Pad by the blur radius so the edges don't fade to black.
            const int pad = 6;
            var visual = new Grid { Width = smallWidth, Height = smallHeight };
            visual.Children.Add(new WpfImage
            {
                Source = source,
                Stretch = Stretch.Fill,
                Margin = new Thickness(-pad),
                Effect = new BlurEffect { Radius = 5, KernelType = KernelType.Gaussian }
            });
            visual.Measure(new WpfSize(smallWidth, smallHeight));
            visual.Arrange(new Rect(0, 0, smallWidth, smallHeight));
            var frosted = new RenderTargetBitmap(smallWidth, smallHeight, 96, 96, PixelFormats.Pbgra32);
            frosted.Render(visual);
            frosted.Freeze();
            return frosted;
        }
        catch (Exception)
        {
            return null; // No glass this time; the tint still makes the ring readable.
        }
    }

    // ---------------------------------------------------------------- the ring

    private void BuildRing()
    {
        var c = new WpfPoint(Radius, Radius);

        // Soft shadow and rim.
        _ring.Children.Add(new Ellipse
        {
            Width = Radius * 2,
            Height = Radius * 2,
            Fill = new SolidColorBrush(Theme.BaseA(0x52)),
            Stroke = new SolidColorBrush(Theme.InkA(0x66)),
            StrokeThickness = Theme.Line(1.2),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.35, Color = Theme.ShadowColor }
        });

        for (var i = 0; i < Sectors.Length; i++)
        {
            var index = i;
            var start = -120 + i * 60;
            var shape = new Path
            {
                Data = Annulus(c, HubDark, Radius - 0.5, start, start + 60),
                Stroke = new SolidColorBrush(Theme.InkA(0x38)),
                StrokeThickness = Theme.Line(1),
                Cursor = Cursors.Hand
            };
            shape.MouseEnter += (_, _) => { _hoverSector = index; PaintSectors(); };
            shape.MouseLeave += (_, _) => { _hoverSector = -1; PaintSectors(); };
            shape.MouseLeftButtonUp += (_, _) => SelectSector(index);
            _sectorShapes[i] = shape;
            _ring.Children.Add(shape);

            var mid = (start + 30) * Math.PI / 180;
            var at = new WpfPoint(c.X + Math.Cos(mid) * 142, c.Y + Math.Sin(mid) * 142);
            var icon = new Path
            {
                Data = Geometry.Parse(Sectors[i].Glyph),
                Stroke = Theme.InkBrush,
                StrokeThickness = 1.7,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
                RenderTransform = new ScaleTransform(1.15, 1.15),
                Effect = TextShadow()
            };
            Canvas.SetLeft(icon, at.X - 13.8);
            Canvas.SetTop(icon, at.Y - 20);
            _sectorIcons[i] = icon;
            _ring.Children.Add(icon);

            var label = new TextBlock
            {
                Text = Sectors[i].Label,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Theme.InkA(0xD9)),
                Width = 80,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
                Effect = TextShadow()
            };
            Canvas.SetLeft(label, at.X - 40);
            Canvas.SetTop(label, at.Y + 12);
            _ring.Children.Add(label);
        }

        // Music lights: bars around the clock that follow the radio (built here, animated while it plays).
        BuildLights(c);

        // Hub: a dark ring carrying the CPU / RAM / disk arcs, and a glass centre with the clock.
        _ring.Children.Add(Circle(c, HubDark, Theme.BaseA(0xB8), Theme.InkA(0x40)));
        foreach (var arc in new[] { _cpuArc, _ramArc, _diskArc })
        {
            _ring.Children.Add(arc);
        }

        var hub = Circle(c, HubGlass, Theme.InkA(0x30), Theme.InkA(0x55));
        hub.Cursor = Cursors.Hand;
        // Press and drag the centre to move the ring; a plain click clears the search.
        hub.Cursor = Cursors.SizeAll; // no tooltip: the ring opens with the mouse right here, so it would cover the clock
        hub.MouseLeftButtonDown += (_, e) => StartDrag(e, hub);
        hub.MouseMove += (_, _) => ContinueDrag(hub);
        hub.MouseLeftButtonUp += (_, _) =>
        {
            if (EndDrag(hub))
            {
                return;
            }

            if (_query.Length > 0)
            {
                SetQuery(string.Empty);
            }
            else
            {
                StepFace(1); // a plain click flips to the next watch
            }
        };
        // Scroll over the watch to change its face.
        hub.MouseWheel += (_, e) => { OnWatchWheel(e.Delta); e.Handled = true; };
        // Right-click the watch to change its colour.
        hub.MouseRightButtonUp += (_, e) => { StepColour(); e.Handled = true; };
        _hubGlass = hub;
        _ring.Children.Add(hub);

        BuildWatch(c);
    }

    // ---------------------------------------------------------------- music lights

    private const int LightCount = SpectrumTap.BandCount * 2;
    private const double LightInner = HubDark + 4;
    private const double LightLength = 24;
    private readonly Canvas _lights = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Line[] _lightBars = new Line[LightCount];
    private readonly float[] _bandTargets = new float[SpectrumTap.BandCount];
    private readonly float[] _bandShown = new float[SpectrumTap.BandCount];
    private readonly Ellipse _glow = new() { IsHitTestVisible = false, StrokeThickness = 5, Opacity = 0 };
    private readonly DispatcherTimer _lightTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private static readonly SolidColorBrush[] LightBrushes = Enumerable.Range(0, 11).Select(i =>
    {
        // Steel blue when quiet, through to amber when loud.
        var t = i / 10.0;
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(0x90 + 0x6F * t),
            (byte)(Steel.R + (Amber.R - Steel.R) * t),
            (byte)(Steel.G + (Amber.G - Steel.G) * t),
            (byte)(Steel.B + (Amber.B - Steel.B) * t)));
        brush.Freeze();
        return brush;
    }).ToArray();

    private void BuildLights(WpfPoint c)
    {
        _lights.Width = _lights.Height = Radius * 2;
        for (var i = 0; i < LightCount; i++)
        {
            var bar = new Line
            {
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = LightBrushes[0]
            };
            _lightBars[i] = bar;
            _lights.Children.Add(bar);
        }

        _glow.Width = _glow.Height = (HubDark + 1) * 2;
        _glow.Stroke = new SolidColorBrush(Amber);
        _glow.Effect = new BlurEffect { Radius = 12 };
        Canvas.SetLeft(_glow, c.X - HubDark - 1);
        Canvas.SetTop(_glow, c.Y - HubDark - 1);
        _lights.Children.Add(_glow);
        _ring.Children.Add(_lights);

        _lightTimer.Tick += (_, _) => AnimateLights();
    }

    private void UpdateLightsRunning()
    {
        var on = IsVisible && _radio.State == RadioState.Playing;
        _lights.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && !_lightTimer.IsEnabled)
        {
            Array.Clear(_bandShown);
            _lightTimer.Start();
        }
        else if (!on && _lightTimer.IsEnabled)
        {
            _lightTimer.Stop();
        }
    }

    private void AnimateLights()
    {
        _radio.Spectrum.CopyBands(_bandTargets);
        var c = new WpfPoint(Radius, Radius);
        for (var band = 0; band < SpectrumTap.BandCount; band++)
        {
            // Rise fast, fall slowly, like a level meter.
            var target = _bandTargets[band];
            _bandShown[band] = target > _bandShown[band] ? target : _bandShown[band] * 0.86f;
        }

        for (var i = 0; i < LightCount; i++)
        {
            // Mirror left and right, bass at the top.
            var band = i < SpectrumTap.BandCount ? i : LightCount - 1 - i;
            var value = _bandShown[band];
            var angle = (-90 + (i + 0.5) * (360.0 / LightCount)) * Math.PI / 180;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            var length = 2 + LightLength * value;
            var bar = _lightBars[i];
            bar.X1 = c.X + cos * LightInner;
            bar.Y1 = c.Y + sin * LightInner;
            bar.X2 = c.X + cos * (LightInner + length);
            bar.Y2 = c.Y + sin * (LightInner + length);
            bar.Stroke = LightBrushes[(int)Math.Round(value * 10)];
        }

        _glow.Opacity = Math.Clamp(_radio.Spectrum.Level * 1.6, 0, 0.9);
    }

    private static void Recenter(FrameworkElement element, WpfPoint c)
    {
        Canvas.SetLeft(element, c.X - element.ActualWidth / 2);
        Canvas.SetTop(element, c.Y - element.ActualHeight / 2);
    }

    private void PaintSectors()
    {
        for (var i = 0; i < Sectors.Length; i++)
        {
            var active = i == _sector && _query.Length == 0;
            var color = active
                ? Color.FromArgb(0x70, Steel.R, Steel.G, Steel.B)
                : i == _hoverSector ? Theme.InkA(0x30) : Theme.InkA(0x12);
            _sectorShapes[i].Fill = new SolidColorBrush(color);
        }
    }

    private void SelectSector(int index)
    {
        _sector = index;
        _selected = 0;
        _query = string.Empty;
        _radioCountry = null;
        _systemView = null;
        _socialView = null;
        _appsPage = 0;
        Refresh();
    }

    private void UpdateHub()
    {
        if (_flash is not null && DateTime.Now > _flashUntil)
        {
            _flash = null;
        }

        UpdateFace();

        _searchText.Text = _query.Length > 0 ? _query : "Search apps, files, drives, radio…";
        _searchText.Opacity = _query.Length > 0 ? 1 : 0.6;
        _searchCaret.Visibility = _query.Length > 0 || IsActive ? Visibility.Visible : Visibility.Collapsed;

        SetHint(_flash ?? (_selected < _items.Count ? OpenHint(_items[_selected]) + _items[_selected].Hint : string.Empty));

        var t = _telemetry.Read();
        SetArc(_cpuArc, HubDark - 3.5, t.CpuUsage);
        SetArc(_ramArc, HubDark - 7, t.RamUsage);
        SetArc(_diskArc, HubDark - 10.5, t.DiskUsage);
    }

    private void SetArc(Path arc, double radius, double percent)
    {
        var fraction = Math.Clamp(percent / 100.0, 0.005, 0.999);
        var c = new WpfPoint(Radius, Radius);
        var start = Polar(c, radius, -90);
        var end = Polar(c, radius, -90 + 360 * fraction);
        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new ArcSegment(end, new WpfSize(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        arc.Data = new PathGeometry([figure]);
    }

    private void Flash(string message)
    {
        _flash = message;
        _flashUntil = DateTime.Now.AddSeconds(4);
        UpdateHub();
    }

    // ---------------------------------------------------------------- items

    private void Refresh()
    {
        _items = _query.Length > 0 ? Search(_query) : ItemsFor(Sectors[_sector].Name);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _items.Count - 1));
        PaintSectors();
        BuildBubbles();
        UpdateHub();
    }

    private List<RingItem> ItemsFor(string sector) => sector switch
    {
        "apps" => FavoriteItems(),
        "web" => SocialItems(),
        "files" => FolderItems(),
        "radio" => RadioItems(),
        "ask" => AskItems(),
        _ => _systemView switch { "power" => PowerItems(), "controls" => ControlItems(), "shortcuts" => ShortcutItems(), "world" => WorldItems(), _ => SystemItems() }
    };

    private List<RingItem> FavoriteItems()
    {
        if (!_catalog.IsLoaded)
        {
            return [Info("Loading apps…", Glyphs.Apps, "Reading your Start menu")];
        }

        var items = (_settings.FavoriteApps ?? [])
            .Select(key => _catalog.Find(key))
            .OfType<AppGroup>()
            .Select(group => AppItem(group, pinnedView: true))
            .ToList();
        items.AddRange(CustomPinItems("apps"));

        if (items.Count < RingSettings.MaxFavorites)
        {
            items.Add(Info("Pin", Glyphs.Plus, "Drag a shortcut onto the ring, or copy it (Ctrl+C) and press Ctrl+V here · or type a name and right-click it"));
        }

        if (items.Count <= MaxBubbles)
        {
            _appsPage = 0;
            return items;
        }

        // More than fit around the ring: 7 and "More", then Back and the rest.
        if (_appsPage == 0)
        {
            return items.Take(MaxBubbles - 1).Append(new RingItem
            {
                Title = "More",
                Glyph = Glyphs.Apps,
                Badge = $"+{items.Count - (MaxBubbles - 1)}",
                Hint = "The rest of your pinned apps",
                ClosesRing = false,
                Rebuild = true,
                Activate = () => { _appsPage = 1; _selected = 1; }
            }).ToList();
        }

        return items.Skip(MaxBubbles - 1).Take(MaxBubbles - 1).Prepend(new RingItem
        {
            Title = "Back",
            Glyph = Glyphs.Back,
            Hint = "Back to the first apps (Esc)",
            ClosesRing = false,
            Rebuild = true,
            Activate = () => { _appsPage = 0; }
        }).ToList();
    }

    private List<RingItem> SocialItems()
    {
        if (_socialView == "browsers")
        {
            return BrowserItems();
        }

        // Your pinned browser accounts first (default: your two Chrome accounts), then Browsers, then social apps.
        var items = _settings.PinnedBrowserProfiles
            .Select(BrowserProfiles.Find)
            .OfType<BrowserProfile>()
            .Select(BrowserItem)
            .ToList();
        items.AddRange(CustomPinItems("web"));
        items.Add(new RingItem
        {
            Title = "Browsers",
            Glyph = Glyphs.Web,
            Badge = $"{BrowserProfiles.All().Count} profiles",
            Hint = "Every Chrome and Edge profile · right-click one to put it here in Social",
            ClosesRing = false,
            Rebuild = true,
            Activate = () => { _socialView = "browsers"; _selected = 1; }
        });

        if (!_catalog.IsLoaded)
        {
            items.Add(Info("Loading apps…", Glyphs.Web, "Reading your Start menu"));
            return items;
        }

        items.AddRange(_catalog.Groups
            .Where(group => group.IsSocial)
            .OrderByDescending(group => _settings.LaunchCounts.GetValueOrDefault(group.Key))
            .ThenBy(group => group.Name)
            .Take(MaxBubbles - items.Count)
            .Select(group => AppItem(group, pinnedView: false)));
        return items;
    }

    private string? _socialView; // null = Social home, "browsers" = every browser profile

    private List<RingItem> BrowserItems()
    {
        // Pinned accounts first, then Chrome, then Edge.
        var pinned = _settings.PinnedBrowserProfiles;
        var profiles = BrowserProfiles.All()
            .OrderBy(p => pinned.IndexOf(p.Key) is var i and >= 0 ? i : 100)
            .ThenBy(p => p.Browser)
            .Take(MaxBubbles - 1)
            .Select(BrowserItem);
        return profiles.Prepend(new RingItem
        {
            Title = "Back",
            Glyph = Glyphs.Back,
            Hint = "Back to Social (Esc)",
            ClosesRing = false,
            Rebuild = true,
            Activate = () => { _socialView = null; }
        }).ToList();
    }

    /// <summary>A browser profile with its picture: click opens the browser in that account; right-click pins it to Social.</summary>
    private RingItem BrowserItem(BrowserProfile profile)
    {
        var pinned = _settings.PinnedBrowserProfiles.Contains(profile.Key);
        return new RingItem
        {
            Title = profile.DisplayName,
            Badge = profile.Browser,
            Subtitle = $"{profile.Browser} · {profile.Name}",
            Glyph = Glyphs.Web,
            IconLoader = () => BrowserProfiles.PictureAsync(profile),
            RoundIcon = true,
            CountKey = "browser:" + profile.Key,
            Hint = $"Open {profile.Browser} as {profile.DisplayName} · right-click: {(pinned ? "remove from" : "show in")} Social",
            Activate = () =>
            {
                // Remember the window this opens as this account's, even before the browser shows whose it is.
                WindowTracker.ClaimNextWindow(profile, WindowTracker.ListTopWindows().Select(w => w.Handle).ToHashSet());
                profile.Open();
            },
            OpenWindows = windows => WindowTracker.For(profile, windows),
            Secondary = () =>
            {
                if (!_settings.PinnedBrowserProfiles.Remove(profile.Key))
                {
                    _settings.PinnedBrowserProfiles.Add(profile.Key);
                }

                _settings.Save();
                Refresh();
                return _settings.PinnedBrowserProfiles.Contains(profile.Key)
                    ? $"{profile.DisplayName} ({profile.Browser}) now shows in Social"
                    : $"{profile.DisplayName} ({profile.Browser}) removed from Social";
            }
        };
    }

    /// <summary>What you added to Files first, then the usual folders, up to 8.</summary>
    private List<RingItem> FolderItems() => CustomPinItems("files").Concat(StandardFolders()).Take(MaxBubbles).ToList();

    private static List<RingItem> StandardFolders()
    {
        (string Title, string Path)[] folders =
        [
            ("Desktop", "shell:Desktop"),
            ("Documents", "shell:Personal"),
            ("Downloads", "shell:Downloads"),
            ("Pictures", "shell:My Pictures"),
            ("Music", "shell:My Music"),
            ("Videos", "shell:My Video"),
            ("This PC", "shell:MyComputerFolder"),
            ("Recycle Bin", "shell:RecycleBinFolder")
        ];

        return folders.Select(folder => new RingItem
        {
            Title = folder.Title,
            IconPath = folder.Path,
            Hint = $"Open {folder.Title}",
            CountKey = "folder:" + folder.Path,
            Activate = () => SystemActions.OpenFolder(folder.Path)
        }).ToList();
    }

    private const int MaxBubbles = 8;

    private List<RingItem> RadioItems()
    {
        var items = new List<RingItem>();
        var playing = _radio.Current is { } current && _radio.State != RadioState.Stopped;
        if (playing)
        {
            items.Add(NowPlayingItem(_radio.Current!));
        }

        bool NotPlaying(Station s) => !playing || s.Id != _radio.Current!.Id;

        if (_radioCountry is { } country)
        {
            // One country's most-played stations.
            items.Insert(0, new RingItem
            {
                Title = "Back",
                Glyph = Glyphs.Back,
                Hint = "Back to favourites and countries (Esc)",
                Activate = () => { _radioCountry = null; },
                ClosesRing = false,
                Rebuild = true
            });

            var top = _radio.CachedTop(country);
            if (top is null)
            {
                _ = _radio.GetTopAsync(country).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                {
                    if (_radioCountry == country && IsVisible && _query.Length == 0)
                    {
                        Refresh();
                    }
                }), TaskScheduler.Default);
                items.Add(Info("Loading…", Glyphs.Radio, $"Fetching stations in {RadioService.CountryName(country)}"));
            }
            else if (top.Count == 0)
            {
                items.Add(Info("Offline", Glyphs.Radio, "Couldn't reach the station directory. Check the internet connection"));
            }
            else
            {
                items.AddRange(top.Where(NotPlaying).Take(MaxBubbles - items.Count).Select(StationItem));
            }

            return items;
        }

        // Home: favourites and recent stations, then a bubble per country, then Find.
        var countries = _settings.RadioCountries.Append(string.Empty).ToList();
        var room = MaxBubbles - items.Count - countries.Count - 1;
        items.AddRange(_settings.RadioFavorites
            .Concat(_settings.RadioRecent)
            .Where(NotPlaying)
            .DistinctBy(s => s.Id)
            .Take(Math.Max(0, room))
            .Select(StationItem));

        items.AddRange(countries.Select(code => new RingItem
        {
            Title = RadioService.CountryName(code),
            Monogram = code.Length == 0 ? null : code,
            Glyph = code.Length == 0 ? Glyphs.Web : null,
            Hint = code.Length == 0 ? "Most-played stations worldwide" : $"Most-played stations in {RadioService.CountryName(code)}",
            Activate = () => { _radioCountry = code; _selected = 1; },
            ClosesRing = false,
            Rebuild = true
        }));

        items.Add(new RingItem
        {
            Title = "Find",
            Glyph = Glyphs.Search,
            Hint = "Type r and a station, genre or country, e.g. \"r jazz\"",
            Activate = () => SetQuery("r "),
            ClosesRing = false
        });
        return items;
    }

    private RingItem StationItem(Station station)
    {
        var favorite = _radio.IsFavorite(station);
        var where = string.Join(" · ", new[] { station.Country, station.Genre }.Where(s => s.Length > 0));
        return new RingItem
        {
            Title = station.DisplayName,
            Badge = (favorite ? "★ " : string.Empty) + station.Badge,
            Glyph = Glyphs.Radio,
            IconLoader = () => _radio.GetLogoAsync(station),
            CountKey = "radio:" + station.Id,
            Hint = $"{where} · right-click: {(favorite ? "remove from" : "save to")} favourites",
            Activate = () => _radio.Play(station),
            ClosesRing = false,
            Rebuild = true,
            Secondary = () =>
            {
                var message = _radio.ToggleFavorite(station);
                Refresh();
                return message;
            }
        };
    }

    private RingItem NowPlayingItem(Station station) => new()
    {
        Title = station.DisplayName,
        Glyph = Glyphs.Radio,
        IconLoader = () => _radio.GetLogoAsync(station),
        Hint = "Now playing · scroll or +/- for radio volume · click to stop · right-click: favourite",
        Status = () => _radio.StatusText,
        Adjust = step => _radio.Volume += step * 5,
        Activate = _radio.Stop,
        ClosesRing = false,
        Rebuild = true,
        Secondary = () =>
        {
            var message = _radio.ToggleFavorite(station);
            Refresh();
            return message;
        }
    };

    private List<RingItem> RadioSearch(string term)
    {
        term = term.Trim();
        if (term.Length < 2)
        {
            return [Info("Radio search", Glyphs.Radio, "Type a station, genre or country, e.g. \"r jazz\" or \"r malta\"")];
        }

        if (string.Equals(_radioResultsFor, term, StringComparison.OrdinalIgnoreCase))
        {
            return _radioResults.Count > 0 ? _radioResults : [Info("No stations", Glyphs.Radio, $"Nothing playable found for \"{term}\"")];
        }

        _radioSearch?.Cancel();
        var cancellation = _radioSearch = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cancellation.Token); // wait until typing pauses
                var stations = await _radio.SearchAsync(term, cancellation.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _radioResultsFor = term;
                    _radioResults = stations.Select(StationItem).ToList();
                    if (_query.StartsWith("r ", StringComparison.OrdinalIgnoreCase))
                    {
                        Refresh();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });

        return [Info("Searching…", Glyphs.Search, $"Looking for \"{term}\" stations")];
    }

    private List<RingItem> AskItems() =>
    [
        new RingItem
        {
            Title = "Read aloud",
            Glyph = Glyphs.Read,
            Hint = "Reads copied text aloud: select an article, press Ctrl+C, then click here",
            Status = () => ReadAloud.IsSpeaking ? "reading" : "copied text",
            ClosesRing = false,
            Activate = () =>
            {
                string? text = null;
                try
                {
                    text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                }

                Flash(ReadAloud.Toggle(text));
            }
        },
        Info("Assistant", Glyphs.Mic, "Talk or type to a local AI (Ollama): find files, play radio, open apps. Coming next")
    ];

    private List<RingItem> SystemItems() =>
    [
        new RingItem
        {
            Title = "Screenshot", Glyph = Glyphs.Camera, CountKey = "sys:shot",
            Hint = "Whole desktop → Pictures\\Screenshots, and copied",
            Activate = () => Delay(350, () =>
            {
                var path = SystemActions.CaptureDesktop();
                _notify($"Screenshot saved and copied: {System.IO.Path.GetFileName(path)}");
            })
        },
        Action("Snip area", Glyphs.Snip, "sys:snip", "Select part of the screen", () => Delay(250, SystemActions.SnipArea)),
        WorldMenuItem(),
        Volume("Volume", Glyphs.Speaker, AudioControl.Speakers, "Speakers"),
        new RingItem
        {
            Title = "Controls",
            Glyph = Glyphs.Sliders,
            Hint = "Volume, mic, brightness, Wi-Fi and Bluetooth",
            ClosesRing = false,
            Rebuild = true,
            Activate = () => { _systemView = "controls"; _selected = 1; Connectivity.Refresh(); }
        },
        new RingItem
        {
            Title = "Shortcuts",
            Glyph = Glyphs.Keyboard,
            Hint = "Windows keys in one click: show desktop, task view, Explorer, settings…",
            ClosesRing = false,
            Rebuild = true,
            Activate = () => { _systemView = "shortcuts"; _selected = 1; }
        },
        new RingItem
        {
            Title = "Timer",
            Glyph = Glyphs.Timer,
            CountKey = "sys:timer",
            Hint = "Work timer: shows on the watch and rings when time is up · or type t 2h",
            Status = () => _timer.IsRinging ? "ringing" : _timer.IsRunning ? FocusTimer.Format(_timer.Remaining) : _timer.IsPaused ? "paused" : FocusTimer.Describe(_timer.Duration),
            ClosesRing = false,
            Activate = () => { SetFace(WatchFace.Timer, remember: false); Flash("Press the button on the watch to start · scroll it to set the time"); }
        },
        new RingItem
        {
            Title = "Power",
            Glyph = Glyphs.Power,
            Hint = "Lock, sleep, restart or shut down",
            ClosesRing = false,
            Rebuild = true,
            Activate = () => { _systemView = "power"; _selected = 1; }
        }
    ];

    private RingItem BackToSystem() => new()
    {
        Title = "Back",
        Glyph = Glyphs.Back,
        Hint = "Back to System (Esc)",
        ClosesRing = false,
        Rebuild = true,
        Activate = () => { _systemView = null; }
    };

    private RingItem BrightnessItem() => new()
    {
        Title = "Brightness",
        Glyph = Glyphs.Sun,
        Hint = "Screen brightness: scroll or +/- · works on monitors with DDC/CI turned on",
        Status = () => BrightnessControl.Status,
        Level = () => BrightnessControl.Level,
        Adjust = step => BrightnessControl.Change(step * 10),
        Activate = BrightnessControl.Warm,
        ClosesRing = false
    };

    private List<RingItem> ControlItems() =>
    [
        BackToSystem(),
        Volume("Volume", Glyphs.Speaker, AudioControl.Speakers, "Speakers"),
        Volume("Mic", Glyphs.MicSmall, AudioControl.Microphone, "Microphone"),
        BrightnessItem(),
        RadioSwitch("Wi-Fi", Glyphs.Wifi, Windows.Devices.Radios.RadioKind.WiFi, () => Connectivity.WifiStatus, "ms-settings:network-wifi"),
        RadioSwitch("Bluetooth", Glyphs.Bluetooth, Windows.Devices.Radios.RadioKind.Bluetooth, () => Connectivity.BluetoothStatus, "ms-settings:bluetooth"),
        Action("Sound settings", Glyphs.Gear, "sys:sound", "Speakers, headphones and microphones", () => SystemActions.Open("ms-settings:sound"))
    ];

    /// <summary>Wi-Fi or Bluetooth: click to switch on/off, right-click for its settings page.</summary>
    private RingItem RadioSwitch(string title, string glyph, Windows.Devices.Radios.RadioKind kind, Func<string> status, string settingsPage) => new()
    {
        Title = title,
        Glyph = glyph,
        Hint = (kind == Windows.Devices.Radios.RadioKind.WiFi && Connectivity.NetworkName() is { } network ? $"Connected to {network} · " : string.Empty)
               + $"Click to switch {title} on or off · right-click: {title} settings",
        Status = status,
        ClosesRing = false,
        Activate = () =>
        {
            Flash($"Switching {title}…");
            _ = Connectivity.ToggleAsync(kind).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                Flash(t.Result);
                RefreshLiveBadges();
            }), TaskScheduler.Default);
        },
        Secondary = () => { SystemActions.Open(settingsPage); return null; }
    };

    private static readonly (string Title, string Glyph, string Hint, byte[] Keys)[] WindowsKeys =
    [
        ("Show desktop", Glyphs.Desktop, "Win+D: hide all windows / bring them back", [0x5B, (byte)'D']),
        ("Task view", Glyphs.Apps, "Win+Tab: all open windows and desktops", [0x5B, 0x09]),
        ("File Explorer", Glyphs.Files, "Win+E", [0x5B, (byte)'E']),
        ("Settings", Glyphs.Gear, "Win+I", [0x5B, (byte)'I']),
        ("Quick settings", Glyphs.Sliders, "Win+A: Windows quick settings panel", [0x5B, (byte)'A']),
        ("Notifications", Glyphs.Bell, "Win+N: notifications and calendar", [0x5B, (byte)'N']),
        ("Emoji", Glyphs.Smile, "Win+. : emoji and symbols", [0x5B, 0xBE]),
        ("Run", Glyphs.Search, "Win+R: run a command", [0x5B, (byte)'R']),
        ("Project screen", Glyphs.Desktop, "Win+P: duplicate or extend to another screen", [0x5B, (byte)'P']),
        ("Cast", Glyphs.Desktop, "Win+K: cast to a TV or wireless display", [0x5B, (byte)'K']),
        ("Clipboard history", Glyphs.Clipboard, "Win+V", [0x5B, (byte)'V']),
        ("Task Manager", Glyphs.Gauge, "Ctrl+Shift+Esc", [0x11, 0x10, 0x1B])
    ];

    /// <summary>A Windows key shortcut: closes the ring first so the keys reach Windows, not the ring.</summary>
    private RingItem KeyShortcut((string Title, string Glyph, string Hint, byte[] Keys) key) => new()
    {
        Title = key.Title,
        Glyph = key.Glyph,
        Hint = key.Hint,
        CountKey = "key:" + key.Title,
        Activate = () => Delay(250, () => NativeMethods.SendKeyCombo(key.Keys))
    };

    private List<RingItem> ShortcutItems() =>
        WindowsKeys.Take(6).Select(KeyShortcut)
            .Append(Action("Record screen", Glyphs.Record, "sys:record", "Record the screen with Snipping Tool (Win+Shift+R)", () => Delay(300, SystemActions.RecordScreen)))
            .Prepend(BackToSystem()).ToList();

    private List<RingItem> PowerItems() =>
    [
        new RingItem
        {
            Title = "Back",
            Glyph = Glyphs.Back,
            Hint = "Back to System (Esc)",
            ClosesRing = false,
            Rebuild = true,
            Activate = () => { _systemView = null; }
        },
        Action("Lock", Glyphs.Lock, "sys:lock", "Lock the PC", SystemActions.Lock),
        Action("Sleep", Glyphs.Power, "sys:sleep", "Put the PC to sleep", () => Delay(400, () => System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false))),
        Confirm("Restart", Glyphs.Restart, "restart", "Restart the PC (switch off and back on)", () => SystemActions.Open("shutdown.exe", "/r /t 0")),
        Confirm("Shut down", Glyphs.Power, "shut down", "Switch the PC off", () => SystemActions.Open("shutdown.exe", "/s /t 0"))
    ];

    /// <summary>System actions that don't fit around the ring but are found by typing.</summary>
    private List<RingItem> MoreSystemItems() =>
    [
        Action("Task Manager", Glyphs.Gauge, "sys:taskmgr", "Open Task Manager", () => SystemActions.Open("taskmgr.exe")),
        Action("Settings", Glyphs.Gear, "sys:settings", "Open Windows Settings", () => SystemActions.Open("ms-settings:")),
        Action("Sound settings", Glyphs.Speaker, "sys:sound", "Sound settings", () => SystemActions.Open("ms-settings:sound")),
        Action("Display settings", Glyphs.Sun, "sys:display", "Display and brightness settings", () => SystemActions.Open("ms-settings:display")),
        Action("Clipboard history", Glyphs.Clipboard, "sys:clip", "Clipboard history (Win+V)", () => Delay(250, SystemActions.ClipboardHistory)),
        Action("Customise", Glyphs.Sliders, "sys:customise", "Themes, watch, places, radio, accounts, hotkey, save your setup", () => ((App)System.Windows.Application.Current).OpenCustomise())
    ];

    /// <summary>A risky action that needs a second click within a few seconds.</summary>
    private RingItem Confirm(string title, string glyph, string verb, string hint, Action run) => new()
    {
        Title = title,
        Glyph = glyph,
        Hint = $"{hint} · asks you to click twice",
        ClosesRing = false,
        Activate = () =>
        {
            if (_armed == title && DateTime.Now < _armedUntil)
            {
                _armed = null;
                HideRing();
                run();
                return;
            }

            _armed = title;
            _armedUntil = DateTime.Now.AddSeconds(4);
            Flash($"Click {title} again to {verb}. Save your work first");
        }
    };

    private static RingItem Action(string title, string glyph, string key, string hint, Action run) =>
        new() { Title = title, Glyph = glyph, CountKey = key, Hint = hint, Activate = run };

    private static RingItem Volume(string title, string glyph, AudioControl audio, string device) => new()
    {
        Title = title,
        Glyph = glyph,
        Hint = $"{device}: scroll or +/- to change · click to mute · right-click: sound settings",
        Status = () => audio.Status,
        Level = () => audio.IsMuted ? 0 : audio.Level / 100.0,
        Adjust = step => audio.Change(step * 4),
        Activate = audio.ToggleMute,
        ClosesRing = false,
        Secondary = () => { SystemActions.Open("ms-settings:sound"); return null; }
    };

    private static RingItem Info(string title, string glyph, string hint) =>
        new() { Title = title, Glyph = glyph, Hint = hint, Activate = () => { }, ClosesRing = false };

    private void Delay(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _notify($"Couldn't do that: {ex.Message}");
            }
        };
        timer.Start();
    }

    private RingItem AppItem(AppGroup group, bool pinnedView)
    {
        var primary = group.Primary;
        var alternative = group.Variants.Skip(1).FirstOrDefault();
        var pinned = _settings.FavoriteApps?.Contains(group.Key) == true;

        string secondaryHint;
        Func<string?> secondary;
        if (pinnedView)
        {
            secondaryHint = "right-click: unpin";
            secondary = () => TogglePin(group);
        }
        else if (alternative is not null)
        {
            secondaryHint = $"right-click: {alternative.SourceLabel}";
            secondary = () => { CloseAfterLaunch(); alternative.Launch(); return null; };
        }
        else
        {
            secondaryHint = pinned ? "right-click: unpin" : "right-click: pin to Apps";
            secondary = () => TogglePin(group);
        }

        return new RingItem
        {
            Title = group.Name,
            Badge = group.Variants.Count > 1 ? primary.ShortSourceLabel : null,
            IconPath = primary.ShellPath,
            CountKey = group.Key,
            Hint = $"{primary.SourceLabel} · {secondaryHint}",
            Activate = primary.Launch,
            OpenWindows = windows => group.Variants.SelectMany(v => WindowTracker.For(v, windows)).DistinctBy(w => w.Handle).ToList(),
            Secondary = secondary
        };
    }

    private string TogglePin(AppGroup group)
    {
        var favorites = _settings.FavoriteApps ??= [];
        string message;
        if (favorites.Remove(group.Key))
        {
            message = $"Unpinned {group.Name}";
        }
        else if (favorites.Count + _settings.CustomPins.Count(p => p.Section == "apps") >= RingSettings.MaxFavorites)
        {
            return $"Apps is full ({RingSettings.MaxFavorites}). Right-click one there to unpin it first";
        }
        else
        {
            favorites.Add(group.Key);
            message = $"Pinned {group.Name} to Apps";
        }

        _settings.Save();
        Refresh();
        return message;
    }

    private void EnsureDefaultFavorites()
    {
        if (_settings.FavoriteApps is not null || !_catalog.IsLoaded)
        {
            return;
        }

        string[] suggestions = ["microsoft edge", "google chrome", "file explorer", "settings", "whatsapp", "spotify", "calculator", "notepad"];
        _settings.FavoriteApps = suggestions.Where(key => _catalog.Find(key) is not null).Take(8).ToList();
        _settings.Save();
    }

    // ---------------------------------------------------------------- search

    private List<RingItem> Search(string query)
    {
        if (query.StartsWith("r ", StringComparison.OrdinalIgnoreCase))
        {
            return RadioSearch(query[2..]);
        }

        if (query.StartsWith("f ", StringComparison.OrdinalIgnoreCase))
        {
            return FileItems(query[2..].Trim(), MaxRows - 1, filesOnly: true);
        }

        if (IsPlaceQuery(query))
        {
            return PlaceSearch(query[(query.IndexOf(' ') + 1)..]);
        }

        var timerCommand = System.Text.RegularExpressions.Regex.Match(query, @"^(?:t|timer)\s+(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (timerCommand.Success)
        {
            return TimerSearch(timerCommand.Groups[1].Value);
        }

        var quick = QuickMatches(query).Take(4).ToList();
        quick.AddRange(FileItems(query.Trim(), MaxRows - 1 - quick.Count, filesOnly: false));
        return quick;
    }

    // ---------------------------------------------------------------- file search

    private List<FileHit> _fileHits = [];
    private string _filesFor = string.Empty;
    private string? _filesPending;
    private CancellationTokenSource? _fileSearch;
    private string? _deepFor;
    private bool _deepRunning;
    private CancellationTokenSource? _deepSearch;
    private DateTime _lastDeepRefresh;

    private List<RingItem> FileItems(string term, int max, bool filesOnly)
    {
        if (term.Length < 2)
        {
            return filesOnly ? [Info("Files", Glyphs.Files, "Type part of a file or folder name")] : [];
        }

        if (!string.Equals(_filesFor, term, StringComparison.OrdinalIgnoreCase))
        {
            StartFileSearch(term);
            return filesOnly ? [Info("Searching files…", Glyphs.Search, "Looking in the Windows index")] : [];
        }

        var rows = _fileHits.Take(Math.Max(0, max)).Select(FileItem).ToList();
        if (filesOnly && rows.Count == 0 && !string.Equals(_deepFor, term, StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(Info("Not in the index", Glyphs.Files, "Windows hasn't indexed a match. Try searching all drives"));
        }

        if (term.Length >= 3)
        {
            rows.Add(DeepSearchItem(term));
        }

        return rows;
    }

    private void StartFileSearch(string term)
    {
        if (string.Equals(_filesPending, term, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _fileSearch?.Cancel();
        var cancellation = _fileSearch = new CancellationTokenSource();
        _filesPending = term;
        _ = Task.Run(async () =>
        {
            List<FileHit> hits;
            try
            {
                await Task.Delay(180, cancellation.Token); // wait until typing pauses
                hits = await FileSearch.QueryIndexAsync(term, 12, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                hits = []; // index unavailable: the all-drives search still works
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                _filesPending = null;
                _filesFor = term;
                _fileHits = hits;
                if (_query.Length > 0 && IsVisible)
                {
                    var selected = _selected;
                    Refresh();
                    _selected = Math.Min(selected, Math.Max(0, _items.Count - 1));
                    PaintSelection();
                }
            });
        });
    }

    private RingItem DeepSearchItem(string term)
    {
        var mine = string.Equals(_deepFor, term, StringComparison.OrdinalIgnoreCase);
        var drives = string.Join(" ", DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name.TrimEnd('\\')));
        return new RingItem
        {
            Title = !mine ? $"Search all drives for \"{term}\"" : _deepRunning ? "Searching all drives…" : "All drives searched",
            Subtitle = !mine ? $"{drives} · includes places Windows doesn't index" : $"{_fileHits.Count} found · {drives}",
            Glyph = Glyphs.Search,
            Hint = "Scans every drive by name. Takes a little longer",
            ClosesRing = false,
            Activate = () => StartDeepSearch(term)
        };
    }

    private void StartDeepSearch(string term)
    {
        if (_deepRunning && string.Equals(_deepFor, term, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _deepSearch?.Cancel();
        var cancellation = _deepSearch = new CancellationTokenSource();
        _deepFor = term;
        _deepRunning = true;
        var seen = new HashSet<string>(_fileHits.Select(h => h.Path), StringComparer.OrdinalIgnoreCase);
        var found = new System.Collections.Concurrent.ConcurrentQueue<FileHit>();

        _ = Task.Run(async () =>
        {
            try
            {
                await FileSearch.ScanDrivesAsync(term, 200, found.Enqueue, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellation.IsCancellationRequested)
                {
                    _deepRunning = false;
                    Drain();
                }
            });
        });

        // Show results as they arrive, a few times a second.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            if (cancellation.IsCancellationRequested || !_deepRunning)
            {
                timer.Stop();
            }

            Drain();
        };
        timer.Start();
        Refresh();

        void Drain()
        {
            if (cancellation.IsCancellationRequested || !string.Equals(_filesFor, term, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var added = false;
            while (found.TryDequeue(out var hit))
            {
                if (seen.Add(hit.Path))
                {
                    _fileHits.Add(hit);
                    added = true;
                }
            }

            if ((added || !_deepRunning) && IsVisible && _query.Length > 0 && DateTime.Now - _lastDeepRefresh > TimeSpan.FromMilliseconds(250))
            {
                _lastDeepRefresh = DateTime.Now;
                _fileHits = _fileHits
                    .OrderByDescending(h => h.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(h => h.Modified)
                    .ToList();
                var selected = _selected;
                Refresh();
                _selected = Math.Min(selected, Math.Max(0, _items.Count - 1));
                PaintSelection();
            }
        }
    }

    private static RingItem FileItem(FileHit hit) => new()
    {
        Title = hit.Name,
        Subtitle = $"{hit.Folder}{(hit.Modified > DateTime.MinValue ? " · " + hit.Modified.ToString("dd/MM/yyyy") : string.Empty)}",
        IconPath = hit.Path,
        Hint = "Enter to open · right-click: show in folder",
        Activate = () => SystemActions.Open(hit.Path),
        Secondary = () =>
        {
            SystemActions.Open("explorer.exe", $"/select,\"{hit.Path}\"");
            return null;
        }
    };

    /// <summary>Titles the search would show for a query (used by the self-test).</summary>
    public IReadOnlyList<string> FindTitles(string query) => QuickMatches(query).Select(item => item.Title).ToList();

    private List<RingItem> QuickMatches(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            return [];
        }

        var candidates = new List<(RingItem Item, string Name)>();
        foreach (var group in _catalog.Groups)
        {
            candidates.Add((AppItem(group, pinnedView: false), group.Name));
        }

        var more = MoreSystemItems();
        var keys = WindowsKeys.Where(k => more.All(m => !m.Title.Equals(k.Title, StringComparison.OrdinalIgnoreCase))).Select(KeyShortcut);
        foreach (var item in FolderItems().Concat(SystemItems()).Concat(more).Concat(PowerItems().Skip(1)).Concat(ControlItems().Skip(2).Take(4)).Concat(keys).Concat(AskItems()))
        {
            candidates.Add((item, item.Title));
        }

        foreach (var place in _settings.Places)
        {
            candidates.Add((PlaceItem(place), place.Name));
        }

        foreach (var pin in _settings.CustomPins)
        {
            candidates.Add((CustomPinItem(pin), pin.Name));
        }

        foreach (var station in _settings.RadioFavorites.Concat(_settings.RadioRecent).DistinctBy(s => s.Id))
        {
            candidates.Add((StationItem(station), station.DisplayName));
        }

        return candidates
            .Select(c => (c.Item, Score: Score(c.Name.ToLowerInvariant(), q)))
            .Where(c => c.Score > 0)
            .Select(c => (c.Item, Score: c.Score + Math.Min(200, 25 * _settings.LaunchCounts.GetValueOrDefault(c.Item.CountKey ?? string.Empty))))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Item.Title.Length)
            .Take(8)
            .Select(c => c.Item)
            .ToList();
    }

    private static int Score(string name, string q)
    {
        if (name == q)
        {
            return 1000;
        }

        if (name.StartsWith(q, StringComparison.Ordinal))
        {
            return 800;
        }

        if (name.Contains(' ' + q, StringComparison.Ordinal))
        {
            return 600;
        }

        if (name.Contains(q, StringComparison.Ordinal))
        {
            return 400;
        }

        // Letters in order, e.g. "vsc" → "visual studio code".
        var position = 0;
        foreach (var ch in name)
        {
            if (position < q.Length && ch == q[position])
            {
                position++;
            }
        }

        return position == q.Length && q.Length >= 2 ? 150 : 0;
    }

    private void SetQuery(string query)
    {
        _query = query;
        _selected = 0;
        Refresh();
    }

    // ---------------------------------------------------------------- bubbles

    private void BuildSearchBox()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
        var glass = GlyphPath(Glyphs.Search);
        glass.RenderTransform = new ScaleTransform(0.8, 0.8, 13, 13);
        row.Children.Add(glass);
        _searchText.FontSize = 15;
        _searchText.Foreground = Theme.InkBrush;
        _searchText.Margin = new Thickness(8, 0, 0, 1);
        _searchText.VerticalAlignment = VerticalAlignment.Center;
        _searchText.TextTrimming = TextTrimming.CharacterEllipsis;
        _searchText.MaxWidth = SearchWidth - 80;
        _searchText.Effect = TextShadow();
        row.Children.Add(_searchText);
        _searchCaret.Width = 1.5;
        _searchCaret.Height = 18;
        _searchCaret.Fill = new SolidColorBrush(Amber);
        _searchCaret.Margin = new Thickness(2, 0, 0, 0);
        _searchCaret.VerticalAlignment = VerticalAlignment.Center;
        var blink = new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(550)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        _searchCaret.BeginAnimation(OpacityProperty, blink);
        row.Children.Add(_searchCaret);

        _searchBox.Width = SearchWidth;
        _searchBox.Height = SearchHeight;
        _searchBox.CornerRadius = new CornerRadius(SearchHeight / 2);
        _searchBox.Background = new SolidColorBrush(Theme.BaseA(0x66));
        _searchBox.BorderBrush = new SolidColorBrush(Theme.InkA(0x60));
        _searchBox.BorderThickness = new Thickness(1);
        _searchBox.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.3, Color = Theme.ShadowColor };
        _searchBox.Cursor = Cursors.IBeam;
        _searchBox.Child = row;
        _searchBox.MouseLeftButtonUp += (_, _) => { Activate(); Focus(); Flash("Type to search · f = files only · r = radio"); };
        _root.Children.Add(_searchBox);

        _results.Width = SearchWidth;
        _results.CornerRadius = new CornerRadius(16);
        _results.Background = new SolidColorBrush(Theme.BaseA(0x70));
        _results.BorderBrush = new SolidColorBrush(Theme.InkA(0x50));
        _results.BorderThickness = new Thickness(1);
        _results.Padding = new Thickness(6);
        _results.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.3, Color = Theme.ShadowColor };
        _results.Visibility = Visibility.Collapsed;
        _results.Child = _resultRows;
        _root.Children.Add(_results);
    }

    /// <summary>Search results as rows under the search box (instead of bubbles), so file names and folders are readable.</summary>
    private void BuildResultRows(GeometryGroup clip)
    {
        _resultRows.Children.Clear();
        var rows = _items.Take(MaxRows).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            var index = i;
            var row = new Border
            {
                Height = RowHeight,
                CornerRadius = new CornerRadius(11),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = "row"
            };
            var grid = new Grid { Margin = new Thickness(8, 0, 10, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = IconView(item, 28);
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = item.Title, FontSize = 13.5, Foreground = Theme.InkBrush, TextTrimming = TextTrimming.CharacterEllipsis, Effect = TextShadow() });
            var sub = item.Subtitle ?? item.Status?.Invoke() ?? item.Badge ?? item.Hint;
            if (!string.IsNullOrEmpty(sub))
            {
                text.Children.Add(new TextBlock
                {
                    Text = sub,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Theme.InkA(0xC0)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Effect = TextShadow()
                });
            }

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            row.Child = grid;
            row.MouseEnter += (_, _) => { _selected = index; PaintSelection(); UpdateHub(); };
            row.MouseLeftButtonUp += (_, _) => Run(item);
            row.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (item.Secondary?.Invoke() is { } message)
                {
                    Flash(message);
                }
            };
            _resultRows.Children.Add(row);
            _bubbleViews.Add(row);
        }

        _results.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0)
        {
            var height = rows.Count * RowHeight + 12 + 2;
            clip.Children.Add(new RectangleGeometry(new Rect(_center.X - SearchWidth / 2, SearchTop + SearchHeight + 8, SearchWidth, height), 16, 16));
        }
    }

    /// <summary>The icon for an item at a given size: shell icon, downloaded logo, line glyph, or short text.</summary>
    private FrameworkElement IconView(RingItem item, double size)
    {
        if (item.Monogram is not null)
        {
            return new TextBlock { Text = item.Monogram, FontSize = size * 0.55, FontWeight = FontWeights.SemiBold, Foreground = Theme.InkBrush, HorizontalAlignment = HorizontalAlignment.Center, Effect = TextShadow() };
        }

        var holder = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        var image = new WpfImage { Width = size, Height = size, Source = item.Icon, Clip = item.RoundIcon ? new EllipseGeometry(new WpfPoint(size / 2, size / 2), size / 2, size / 2) : null };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        if (item.IconPath is not null)
        {
            holder.Children.Add(image);
            if (item.Icon is null)
            {
                _ = LoadIcon(item, image);
            }

            return holder;
        }

        var glyph = GlyphPath(item.Glyph ?? Glyphs.Apps);
        glyph.RenderTransform = new ScaleTransform(size / 28, size / 28, 13, 13);
        holder.Children.Add(glyph);
        if (item.IconLoader is not null)
        {
            holder.Children.Add(image);
            if (item.Icon is not null)
            {
                glyph.Visibility = Visibility.Hidden;
            }
            else
            {
                _ = LoadLogo(item, image, glyph);
            }
        }

        return holder;
    }

    private void BuildBubbles()
    {
        _bubbles.Children.Clear();
        _bubbleViews.Clear();
        _levelArcs.Clear();
        _openMarks.Clear();
        _liveBadges.Clear();

        var clip = new GeometryGroup { FillRule = FillRule.Nonzero };
        clip.Children.Add(new EllipseGeometry(_center, Radius, Radius));
        clip.Children.Add(new RectangleGeometry(new Rect(_center.X - SearchWidth / 2, SearchTop, SearchWidth, SearchHeight), SearchHeight / 2, SearchHeight / 2));

        if (_query.Length > 0)
        {
            BuildResultRows(clip);
            _glass.Clip = clip;
            PaintSelection();
            return;
        }

        _results.Visibility = Visibility.Collapsed;
        var count = _items.Count;
        // Fan from upper side to just above the search box: up to 8 bubbles, spaced so they don't touch.
        var step = count <= 1 ? 0 : Math.Min(21.5, 122.0 / (count - 1));
        var spread = step * Math.Max(0, count - 1);
        var animate = SystemParameters.ClientAreaAnimation;

        for (var i = 0; i < count; i++)
        {
            var item = _items[i];
            var index = i;
            var angle = -19 - spread / 2 + i * step;
            if (_mirrored)
            {
                angle = 180 - angle;
            }

            var at = Polar(_center, BubbleOrbit, angle);
            clip.Children.Add(new EllipseGeometry(at, BubbleRadius, BubbleRadius));

            var view = BubbleView(item);
            Canvas.SetLeft(view, at.X - BubbleRadius);
            Canvas.SetTop(view, at.Y - BubbleRadius);
            view.MouseEnter += (_, _) => { _selected = index; PaintSelection(); UpdateHub(); };
            view.MouseLeftButtonUp += (_, _) => Run(item);
            view.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (item.Secondary?.Invoke() is { } message)
                {
                    Flash(message);
                }
            };

            _bubbles.Children.Add(view);
            _bubbleViews.Add(view);

            if (animate)
            {
                var direction = new Vector(at.X - _center.X, at.Y - _center.Y);
                direction.Normalize();
                var move = new TranslateTransform(-direction.X * 22, -direction.Y * 22);
                view.RenderTransform = move;
                view.Opacity = 0;
                var delay = TimeSpan.FromMilliseconds(i * 22);
                Animate(view, OpacityProperty, 0, 1, 160, delay);
                Animate(move, TranslateTransform.XProperty, move.X, 0, 200, delay);
                Animate(move, TranslateTransform.YProperty, move.Y, 0, 200, delay);
            }
        }

        _glass.Clip = clip;
        PaintSelection();
    }

    private FrameworkElement BubbleView(RingItem item)
    {
        var grid = new Grid { Width = BubbleRadius * 2, Height = BubbleRadius * 2, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        grid.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Theme.BaseA(0x5C)),
            Stroke = new SolidColorBrush(Theme.InkA(0x50)),
            StrokeThickness = Theme.Line(1),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.3, Color = Theme.ShadowColor }
        });
        grid.Children.Add(new Ellipse
        {
            Name = "Selection",
            Margin = new Thickness(3),
            Stroke = new SolidColorBrush(Amber),
            StrokeThickness = 2,
            Visibility = Visibility.Hidden
        });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        if (item.Monogram is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.Monogram,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.InkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = TextShadow()
            });
        }
        else if (item.IconLoader is not null)
        {
            // Glyph first; swap in the logo if it downloads.
            var holder = new Grid { Width = item.RoundIcon ? 34 : 30, Height = item.RoundIcon ? 34 : 30, HorizontalAlignment = HorizontalAlignment.Center };
            var glyph = GlyphPath(item.Glyph ?? Glyphs.Radio);
            var logo = new WpfImage { Width = item.RoundIcon ? 34 : 30, Height = item.RoundIcon ? 34 : 30, Source = item.Icon, Clip = item.RoundIcon ? new EllipseGeometry(new WpfPoint(17, 17), 17, 17) : null };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            holder.Children.Add(glyph);
            holder.Children.Add(logo);
            stack.Children.Add(holder);
            if (item.Icon is not null)
            {
                glyph.Visibility = Visibility.Hidden;
            }
            else
            {
                _ = LoadLogo(item, logo, glyph);
            }
        }
        else if (item.Glyph is not null && item.IconPath is null)
        {
            stack.Children.Add(new Path
            {
                Data = Geometry.Parse(item.Glyph),
                Stroke = Theme.InkBrush,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 26,
                Height = 26,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransform = new ScaleTransform(1.08, 1.08),
                Effect = TextShadow()
            });
        }
        else
        {
            var image = new WpfImage { Width = 30, Height = 30, Source = item.Icon };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            stack.Children.Add(image);
            if (item.Icon is null && item.IconPath is not null)
            {
                _ = LoadIcon(item, image);
            }
        }

        stack.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 10.5,
            Foreground = Theme.InkBrush,
            MaxWidth = BubbleRadius * 2 - 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
            Effect = TextShadow()
        });

        var badgeText = item.Status?.Invoke() ?? item.Badge;
        if (!string.IsNullOrEmpty(badgeText))
        {
            var badge = new TextBlock
            {
                Text = badgeText,
                FontSize = item.Status is null ? 8.5 : 9.5,
                FontWeight = item.Status is null ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = new SolidColorBrush(item.Status is null ? Theme.InkA(0xD0) : Amber),
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = TextShadow()
            };
            stack.Children.Add(badge);
            if (item.Status is not null)
            {
                _liveBadges.Add((item, badge));
            }
        }

        if (item.Adjust is not null)
        {
            grid.MouseWheel += (_, e) =>
            {
                item.Adjust(Math.Sign(e.Delta));
                RefreshLiveBadges();
                e.Handled = true;
            };
        }

        grid.Children.Add(stack);

        // Level ring: a faint full circle with an orange arc showing how far up it is (volume, mic, brightness).
        if (item.Level is not null)
        {
            grid.Children.Add(new Ellipse
            {
                Margin = new Thickness(2),
                Stroke = new SolidColorBrush(Theme.InkA(0x30)),
                StrokeThickness = 3,
                IsHitTestVisible = false
            });
            var arc = new Path
            {
                Stroke = AmberBrush,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            grid.Children.Add(arc);
            _levelArcs.Add((item, arc));
            DrawLevel(arc, item.Level());
        }

        AddOpenMarks(item, grid);
        return grid;
    }

    private readonly List<(RingItem Item, Path Arc)> _levelArcs = [];

    private static void DrawLevel(Path arc, double? level)
    {
        if (level is not { } value || value <= 0.005)
        {
            arc.Data = null;
            return;
        }

        var r = BubbleRadius - 3.5;
        var c = new WpfPoint(BubbleRadius, BubbleRadius);
        var sweep = Math.Min(359.9, 360 * Math.Clamp(value, 0, 1));
        var end = Polar(c, r, -90 + sweep);
        var figure = new PathFigure { StartPoint = Polar(c, r, -90) };
        figure.Segments.Add(new ArcSegment(end, new WpfSize(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        arc.Data = new PathGeometry([figure]);
    }

    private static Path GlyphPath(string data) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = Theme.InkBrush,
        StrokeThickness = 1.6,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 26,
        Height = 26,
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Effect = TextShadow()
    };

    private static async Task LoadLogo(RingItem item, WpfImage image, UIElement glyph)
    {
        var loading = item.IconLoader!();
        if (loading.IsCompletedSuccessfully)
        {
            // Already loaded (cached): show it straight away, no flicker.
            if (loading.Result is { } ready)
            {
                item.Icon = ready;
                image.Source = ready;
                glyph.Visibility = Visibility.Hidden;
            }

            return;
        }

        var logo = await loading;
        if (logo is null)
        {
            return;
        }

        item.Icon = logo;
        await image.Dispatcher.InvokeAsync(() =>
        {
            image.Source = logo;
            glyph.Visibility = Visibility.Hidden;
        });
    }

    private static async Task LoadIcon(RingItem item, WpfImage image)
    {
        var icon = await ShellIcons.GetAsync(item.IconPath!);
        item.Icon = icon;
        await image.Dispatcher.InvokeAsync(() => image.Source = icon);
    }

    private static readonly SolidColorBrush RowSelected = Freeze(new SolidColorBrush(Color.FromArgb(0x38, Amber.R, Amber.G, Amber.B)));
    private static readonly SolidColorBrush AmberBrush = Freeze(new SolidColorBrush(Amber));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void PaintSelection()
    {
        for (var i = 0; i < _bubbleViews.Count; i++)
        {
            if (_bubbleViews[i] is Grid grid && grid.Children[1] is Ellipse ring)
            {
                ring.Visibility = i == _selected ? Visibility.Visible : Visibility.Hidden;
            }
            else if (_bubbleViews[i] is Border { Tag: "row" } row)
            {
                row.Background = i == _selected ? RowSelected : Brushes.Transparent;
                row.BorderBrush = i == _selected ? AmberBrush : Brushes.Transparent;
            }
        }
    }

    private void Run(RingItem item)
    {
        if (!item.ClosesRing)
        {
            if (item.CountKey is not null)
            {
                _settings.CountLaunch(item.CountKey);
            }

            // Show the hint first, so an action can replace it with its own message (e.g. "click again to shut down").
            if (item.Status is null && !item.Rebuild)
            {
                Flash(item.Hint);
            }

            item.Activate();
            if (item.Rebuild)
            {
                Refresh();
            }

            RefreshLiveBadges();
            return;
        }

        if (item.CountKey is not null)
        {
            _settings.CountLaunch(item.CountKey);
        }

        if (SwitchToOpenApp(item))
        {
            return; // already open: brought to the front instead of opening another copy
        }

        CloseAfterLaunch();
        try
        {
            item.Activate();
        }
        catch (Exception ex)
        {
            _notify($"Couldn't open {item.Title}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- keyboard

    private void OnKey(object sender, KeyEventArgs e)
    {
        var count = _items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                if (_query.Length > 0)
                {
                    SetQuery(string.Empty);
                }
                else if (_radioCountry is not null || _systemView is not null || _socialView is not null || _appsPage > 0)
                {
                    _radioCountry = null;
                    _systemView = null;
                    _socialView = null;
                    _appsPage = 0;
                    _selected = 0;
                    Refresh();
                }
                else
                {
                    HideRing();
                }

                break;
            case Key.Enter:
                if (_selected < count)
                {
                    Run(_items[_selected]);
                }

                break;
            case Key.Back:
                if (_query.Length > 0)
                {
                    SetQuery(_query[..^1]);
                }

                break;
            case Key.Up:
                Move(-1);
                break;
            case Key.Down:
                Move(1);
                break;
            case Key.Left when _query.Length == 0:
                SelectSector((_sector + Sectors.Length - 1) % Sectors.Length);
                break;
            case Key.Right when _query.Length == 0:
            case Key.Tab when _query.Length == 0:
                SelectSector((_sector + 1) % Sectors.Length);
                break;
            case Key.Left:
                Move(-1);
                break;
            case Key.Right:
                Move(1);
                break;
            case Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract
                when _query.Length == 0 && _selected < count && _items[_selected].Adjust is { } adjust:
                adjust(e.Key is Key.OemPlus or Key.Add ? 1 : -1);
                RefreshLiveBadges();
                break;
            case Key.V when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                PasteFromClipboard(); // pin copied shortcuts, programs, files or folders
                break;
            case Key.Apps:
                if (_selected < count && _items[_selected].Secondary?.Invoke() is { } message)
                {
                    Flash(message);
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void RefreshLiveBadges()
    {
        foreach (var (item, badge) in _liveBadges)
        {
            badge.Text = item.Status!.Invoke();
        }

        foreach (var (item, arc) in _levelArcs)
        {
            DrawLevel(arc, item.Level!.Invoke());
        }
    }

    private void Move(int delta)
    {
        if (_items.Count == 0)
        {
            return;
        }

        _selected = (_selected + delta + _items.Count) % _items.Count;
        PaintSelection();
        UpdateHub();
    }

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
        {
            return;
        }

        if (_query.Length == 0 && e.Text == " ")
        {
            return;
        }

        SetQuery(_query + e.Text);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- helpers

    private static WpfPoint Polar(WpfPoint c, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new WpfPoint(c.X + Math.Cos(radians) * radius, c.Y + Math.Sin(radians) * radius);
    }

    private static Geometry Annulus(WpfPoint c, double inner, double outer, double from, double to)
    {
        var figure = new PathFigure { StartPoint = Polar(c, outer, from), IsClosed = true };
        figure.Segments.Add(new ArcSegment(Polar(c, outer, to), new WpfSize(outer, outer), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(Polar(c, inner, to), true));
        figure.Segments.Add(new ArcSegment(Polar(c, inner, from), new WpfSize(inner, inner), 0, false, SweepDirection.Counterclockwise, true));
        return new PathGeometry([figure]);
    }

    private static Ellipse Circle(WpfPoint c, double radius, Color fill, Color stroke)
    {
        var ellipse = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 1
        };
        Canvas.SetLeft(ellipse, c.X - radius);
        Canvas.SetTop(ellipse, c.Y - radius);
        return ellipse;
    }

    private static Path Arc(Color color) => new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0xE0, color.R, color.G, color.B)),
        StrokeThickness = 2.2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false
    };

    private static TextBlock HubText(double size, FontWeight weight) => new()
    {
        FontSize = size,
        FontWeight = weight,
        Foreground = Theme.InkBrush,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Effect = TextShadow()
    };

    private static DropShadowEffect TextShadow() => new() { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.75, Color = Theme.ShadowColor };

    private static void Animate(IAnimatable target, DependencyProperty property, double from, double to, int milliseconds, TimeSpan? delay = null)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(property, null);
            return;
        }

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            BeginTime = delay
        };
        target.BeginAnimation(property, animation);
    }
}
