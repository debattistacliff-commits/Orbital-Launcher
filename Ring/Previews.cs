using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace DesktopOrbit.Ring;

/// <summary>Pictures of the ring for the self-test and for previews (drawn without showing the window).</summary>
public sealed partial class RingWindow
{
    /// <summary>Draws one section (bubbles, watch, search box) as it looks when open.</summary>
    public BitmapSource RenderFrameForTest(string sectorName, int width = 640, int height = 640)
    {
        _mirrored = false;
        _center = new WpfPoint(RingOffset, CenterY);
        Canvas.SetLeft(_ring, _center.X - Radius);
        Canvas.SetTop(_ring, _center.Y - Radius);
        Canvas.SetLeft(_hintHost, _center.X - _hintHost.Width / 2);
        Canvas.SetTop(_hintHost, _center.Y + Radius + 10);
        Canvas.SetLeft(_searchBox, _center.X - SearchWidth / 2);
        Canvas.SetTop(_searchBox, SearchTop);
        var pinAt = Polar(_center, Radius - 10, -135);
        Canvas.SetLeft(_pin, pinAt.X - PinRadius);
        Canvas.SetTop(_pin, pinAt.Y - PinRadius);

        _sector = Array.FindIndex(Sectors, s => s.Name == sectorName);
        _systemView = null;
        _socialView = null;
        _query = string.Empty;
        Refresh();
        foreach (var view in _bubbleViews)
        {
            view.BeginAnimation(OpacityProperty, null);
            view.Opacity = 1;
            view.RenderTransform = null;
        }

        _root.Measure(new System.Windows.Size(WindowWidth, WindowHeight));
        _root.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
        _root.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // A desktop-like backdrop so glass themes read as they do on screen.
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x45, 0x55), Color.FromRgb(0x1D, 0x22, 0x2A), 45), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(_root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, Viewbox = new Rect(0, 0, width, height), ViewboxUnits = BrushMappingMode.Absolute },
                null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Draws the watch once per flag, side by side.</summary>
    public void RenderFlagsForTest(string path)
    {
        var (colour, face) = (_colour, _face);
        SetFace(WatchFace.Classic, remember: false);
        var frames = new List<BitmapSource>();
        for (var i = 0; i < Flags.Length; i++)
        {
            _colour = DialColours.Length + i;
            ApplyDial();
            UpdateFace();
            frames.Add(RenderWatchOnly(180));
        }

        _colour = colour;
        SetFace(face, remember: false);
        SaveStrip(frames, path, columns: 7);
    }

    private BitmapSource RenderWatchOnly(int size)
    {
        _ring.Measure(new System.Windows.Size(Radius * 2, Radius * 2));
        _ring.Arrange(new Rect(0, 0, Radius * 2, Radius * 2));
        _ring.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x34)), null, new Rect(0, 0, size, size));
            context.DrawRectangle(new VisualBrush(_ring) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(Radius - size / 2.0, Radius - size / 2.0, size, size) },
                null, new Rect(0, 0, size, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Puts pictures in a grid and saves it as a PNG.</summary>
    public static void SaveStrip(IReadOnlyList<BitmapSource> frames, string path, int columns)
    {
        if (frames.Count == 0)
        {
            return;
        }

        int w = frames[0].PixelWidth, h = frames[0].PixelHeight;
        columns = Math.Min(columns, frames.Count);
        var rows = (frames.Count + columns - 1) / columns;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            for (var i = 0; i < frames.Count; i++)
            {
                context.DrawImage(frames[i], new Rect(i % columns * w, i / columns * h, w, h));
            }
        }

        var all = new RenderTargetBitmap(w * columns, h * rows, 96, 96, PixelFormats.Pbgra32);
        all.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(all));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }
}
