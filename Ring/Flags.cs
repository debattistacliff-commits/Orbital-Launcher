using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace DesktopOrbit.Ring;

/// <summary>
/// Flag and emblem backgrounds for the watch (drawn, so they're sharp at any size), softly darkened so the time stays readable.
/// </summary>
public sealed partial class RingWindow
{
    private static readonly string[] Flags =
    [
        "Malta", "Maltese Cross", "England", "United Kingdom", "Scotland", "Wales", "Ireland", "Italy", "France", "Germany", "Spain", "Portugal", "Netherlands", "Poland",
        "London · Big Ben", "Rome · Colosseum", "Paris · Eiffel Tower", "Valletta", "Sydney · Opera House"
    ];

    /// <summary>A city skyline: evening sky, a dark silhouette of its landmark, warm lights.</summary>
    private static void Landmark(DrawingGroup group, string name)
    {
        var sky = new LinearGradientBrush { StartPoint = new WpfPoint(0, 0), EndPoint = new WpfPoint(0, 1) };
        sky.GradientStops.Add(new GradientStop(Hex("#15203D"), 0));
        sky.GradientStops.Add(new GradientStop(Hex("#3B3F74"), 0.45));
        sky.GradientStops.Add(new GradientStop(Hex("#E0794A"), 0.82));
        sky.GradientStops.Add(new GradientStop(Hex("#F2B36A"), 1));
        group.Children.Add(new GeometryDrawing(sky, null, new RectangleGeometry(new Rect(0, 0, F, F))));
        var dark = new SolidColorBrush(Hex("#10131C"));
        var light = new SolidColorBrush(Hex("#F7D27C"));
        void Silhouette(string path) => group.Children.Add(new GeometryDrawing(dark, null, Geometry.Parse(path)));
        void Glow(string path) => group.Children.Add(new GeometryDrawing(light, null, Geometry.Parse(path)));

        switch (name)
        {
            case "London · Big Ben":
                Silhouette("M0,150 V112 H10 V104 H16 V112 H30 V106 H36 V112 H50 V104 H56 V112 H64 V150 Z");     // Parliament, left
                Silhouette("M86,150 V112 H96 V106 H102 V112 H116 V104 H122 V112 H136 V106 H142 V112 H150 V150 Z"); // Parliament, right
                Silhouette("M64,150 V44 H62 V38 H66 L75,10 L84,38 H88 V44 H86 V150 Z");                          // the tower and spire
                Glow("M75,48 m-8,0 a8,8 0 1,0 16,0 a8,8 0 1,0 -16,0");                                             // lit clock face
                Silhouette("M75,48 L75,43 M75,48 L79,50");
                break;

            case "Rome · Colosseum":
                Silhouette("M8,150 V86 Q75,62 142,80 V96 Q120,92 112,100 V150 Z");
                for (var row = 0; row < 3; row++)
                {
                    for (var arch = 0; arch < 7; arch++)
                    {
                        var x = 16 + arch * 18;
                        var y = 92 + row * 18 - (row == 0 ? Math.Sin((arch + 0.5) / 7 * Math.PI) * 6 : 0);
                        if (x > 108 && row == 0)
                        {
                            continue; // the broken top on the right
                        }

                        Glow(FormattableString.Invariant($"M{x},{y + 12} V{y + 5} A4.5,4.5 0 0 1 {x + 9},{y + 5} V{y + 12} Z"));
                    }
                }

                break;

            case "Paris · Eiffel Tower":
                Silhouette("M73,8 H77 L80,40 L86,78 L96,108 L114,150 H96 Q75,118 54,150 H36 L54,108 L64,78 L70,40 Z");
                Silhouette("M58,78 H92 V83 H58 Z M50,106 H100 V111 H50 Z");
                Glow("M74.5,4 m-2,0 a2,2 0 1,0 4,0 a2,2 0 1,0 -4,0");
                break;

            case "Valletta":
                Silhouette("M0,150 V122 L14,114 H136 L150,122 V150 Z");                                  // bastion walls
                Silhouette("M56,114 V92 H58 A17,17 0 0 1 92,92 H94 V114 Z M73,62 H77 V76 H73 Z");       // the great dome and lantern
                Silhouette("M100,114 V70 H112 V114 Z M100,70 L106,52 L112,70 Z");                         // spire
                Silhouette("M24,114 V100 A8,8 0 0 1 40,100 V114 Z M118,114 V102 H134 V114 Z");            // smaller domes and buildings
                for (var i = 0; i < 6; i++)
                {
                    Glow(FormattableString.Invariant($"M{20 + i * 20},128 h4 v5 h-4 z"));                 // harbour lights
                }

                break;

            case "Sydney · Opera House":
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(Hex("#1B2C4A")), null, new RectangleGeometry(new Rect(0, 124, F, 26)))); // the harbour
                Silhouette("M28,124 H122 V118 H28 Z");
                Silhouette("M34,118 Q44,80 70,72 Q58,96 60,118 Z");
                Silhouette("M58,118 Q70,76 98,70 Q84,96 86,118 Z");
                Silhouette("M86,118 Q96,90 116,86 Q106,104 110,118 Z");
                break;
        }
    }

    private const double F = 150; // flags are drawn on a 150×150 square and stretched to the dial

    /// <summary>Y Ddraig Goch: the official Welsh flag dragon (public domain), stored as a picture.</summary>
    private static void WelshDragon(DrawingGroup group)
    {
        var picture = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/flag-wales.png"));
        picture.Freeze();
        group.Children.Add(new ImageDrawing(picture, new Rect(0, 0, F, F)));
    }

    private static Brush FlagBrush(string flag)
    {
        var group = new DrawingGroup();
        void Box(string colour, double x, double y, double w, double h) =>
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Hex(colour)), null, new RectangleGeometry(new Rect(x, y, w, h))));
        void Shape(string colour, string path) =>
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Hex(colour)), null, Geometry.Parse(path)));
        void Bands(bool vertical, params string[] colours)
        {
            var size = F / colours.Length;
            for (var i = 0; i < colours.Length; i++)
            {
                if (vertical) Box(colours[i], i * size, 0, size + 0.5, F);
                else Box(colours[i], 0, i * size, F, size + 0.5);
            }
        }

        switch (flag)
        {
            case "Malta":
                Bands(true, "#FFFFFF", "#CF142B");
                // George Cross (grey with a red edge) in the corner.
                Shape("#CF142B", "M22,30 h10 v-9 h12 v9 h10 v12 h-10 v9 h-12 v-9 h-10 z");
                Shape("#A9AFB7", "M24,32 h10 v-9 h8 v9 h10 v8 h-10 v9 h-8 v-9 h-10 z");
                break;

            case "Maltese Cross":
                // White eight-pointed cross of the Knights of Malta on red.
                Box("#CF142B", 0, 0, F, F);
                for (var arm = 0; arm < 4; arm++)
                {
                    var geometry = Geometry.Parse("M75,72 L48,22 L75,40 L102,22 Z").Clone();
                    geometry.Transform = new RotateTransform(arm * 90, 75, 75);
                    group.Children.Add(new GeometryDrawing(Brushes.White, null, geometry));
                }

                break;

            case "England":
                Box("#FFFFFF", 0, 0, F, F);
                Box("#CE1124", 0, 62, F, 26);
                Box("#CE1124", 62, 0, 26, F);
                break;

            case "United Kingdom":
                Box("#012169", 0, 0, F, F);
                group.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 30), new LineGeometry(new WpfPoint(0, 0), new WpfPoint(F, F))));
                group.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 30), new LineGeometry(new WpfPoint(F, 0), new WpfPoint(0, F))));
                group.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Hex("#C8102E")), 10), new LineGeometry(new WpfPoint(0, 0), new WpfPoint(F, F))));
                group.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Hex("#C8102E")), 10), new LineGeometry(new WpfPoint(F, 0), new WpfPoint(0, F))));
                Box("#FFFFFF", 0, 55, F, 40);
                Box("#FFFFFF", 55, 0, 40, F);
                Box("#C8102E", 0, 63, F, 24);
                Box("#C8102E", 63, 0, 24, F);
                break;

            case "Scotland":
                Box("#005EB8", 0, 0, F, F);
                group.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 26), new LineGeometry(new WpfPoint(0, 0), new WpfPoint(F, F))));
                group.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 26), new LineGeometry(new WpfPoint(F, 0), new WpfPoint(0, F))));
                break;

            case "Wales":
                Bands(false, "#FFFFFF", "#00AB39");
                WelshDragon(group);
                break;

            case "Ireland": Bands(true, "#169B62", "#FFFFFF", "#FF883E"); break;
            case "Italy": Bands(true, "#009246", "#FFFFFF", "#CE2B37"); break;
            case "France": Bands(true, "#0055A4", "#FFFFFF", "#EF4135"); break;
            case "Germany": Bands(false, "#000000", "#DD0000", "#FFCE00"); break;
            case "Netherlands": Bands(false, "#AE1C28", "#FFFFFF", "#21468B"); break;
            case "Poland": Bands(false, "#FFFFFF", "#DC143C"); break;

            case "Spain":
                Box("#AA151B", 0, 0, F, F);
                Box("#F1BF00", 0, 37.5, F, 75);
                break;

            case "London · Big Ben":
            case "Rome · Colosseum":
            case "Paris · Eiffel Tower":
            case "Valletta":
            case "Sydney · Opera House":
                Landmark(group, flag);
                break;

            case "Portugal":
                Box("#FF0000", 0, 0, F, F);
                Box("#006600", 0, 0, 60, F);
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(Hex("#FFCC00")), null, new EllipseGeometry(new WpfPoint(60, 75), 22, 22)));
                break;
        }

        // Darken softly so white numbers and hands stay readable on light flags.
        group.Children.Add(new GeometryDrawing(new RadialGradientBrush(Color.FromArgb(0x40, 0, 0, 0), Color.FromArgb(0x70, 0, 0, 0)), null, new RectangleGeometry(new Rect(0, 0, F, F))));
        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static Color Hex(string hex) => DesktopOrbit.ShellIntegration.Theme.Parse(hex);
}

public sealed partial class RingWindow
{
    /// <summary>Names of the watch faces, in order (for Customise).</summary>
    public static IReadOnlyList<string> WatchFaceNames => FaceNames;

    /// <summary>Names of the watch backgrounds: colours, then flags (for Customise).</summary>
    public static IReadOnlyList<string> WatchBackgroundNames => Enumerable.Range(0, DialCount).Select(DialName).ToList();
}
