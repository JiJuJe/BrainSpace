using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrainSpace;

internal sealed class Theme(Preferences settings)
{
    public SolidColorBrush Canvas { get; } = Brush(settings.Light ? "#F3F5F8" : "#101319");
    public SolidColorBrush CanvasOutside { get; } = Brush(settings.Light ? "#E2E7EE" : "#0A0D12");
    public SolidColorBrush Bar { get; } = Brush(settings.Light ? "#F9FAFC" : "#13171E");
    public SolidColorBrush Surface { get; } = Brush(settings.Light ? "#FFFFFF" : "#191E27");
    public SolidColorBrush Hover { get; } = Brush(settings.Light ? "#E3EBF5" : "#222A36");
    public SolidColorBrush Text { get; } = Brush(settings.Light ? "#1A2636" : "#F2F5FA");
    public SolidColorBrush Muted { get; } = Brush(settings.Light ? "#4D6075" : "#A1ADBF");
    public SolidColorBrush Border { get; } = Brush(settings.Light ? "#CBD5E2" : "#303947");
    public SolidColorBrush Node { get; } = Brush(settings.Light ? "#FDFEFF" : "#151A22");
    public SolidColorBrush Edge { get; } = Brush(settings.Light ? "#8193A9" : "#667489");
    public SolidColorBrush Dot { get; } = Brush(settings.Light ? "#C1CCDB" : "#35404E");
    public SolidColorBrush Accent { get; } = Brush(ValidColor(settings.Accent, "#83C6FB"));
    public SolidColorBrush Active { get; } = Brush(settings.Light ? "#DBEBFA" : "#203144");

    public static string ValidColor(string? value, string fallback) =>
        value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToArray().All(Uri.IsHexDigit) ? value : fallback;

    public static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}

internal static class Icons
{
    private static readonly Dictionary<string, Geometry> Paths = new Dictionary<string, string>
    {
        ["home"] = "M3 10 L12 3 L21 10 V20 H15 V14 H9 V20 H3 Z",
        ["search"] = "M16.5 16.5 L21 21 M18 11 A7 7 0 1 1 4 11 A7 7 0 1 1 18 11",
        ["plus"] = "M12 5 V19 M5 12 H19", ["minus"] = "M5 12 H19",
        ["more"] = "M5 12 H5.4 M11.8 12 H12.2 M18.6 12 H19",
        ["undo"] = "M9 5 L3 11 L9 17 M3 11 H15 A5 5 0 0 1 15 21", ["redo"] = "M15 5 L21 11 L15 17 M21 11 H9 A5 5 0 0 0 9 21",
        ["back"] = "M15 5 L8 12 L15 19", ["forward"] = "M9 5 L16 12 L9 19",
        ["minimize"] = "M5 13 H19", ["maximize"] = "M5 5 H19 V19 H5 Z", ["restore"] = "M8 5 H19 V16 M5 8 H16 V19 H5 Z",
        ["close"] = "M6 6 L18 18 M18 6 L6 18",
        ["globe"] = "M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M2 12 H22 M12 2 C6.5 7 6.5 17 12 22 C17.5 17 17.5 7 12 2",
        ["select"] = "M5 3 L19 13 L12 14 L9 21 Z", ["pen"] = "M4 16 L16 4 A2.1 2.1 0 0 1 19 4 L20 5 A2.1 2.1 0 0 1 20 8 L8 20 L3 21 Z M14 6 L18 10",
        ["marker"] = "M4 16 L14 6 L19 11 L9 21 H4 Z M12 8 L17 3 L22 8 L17 13 M5 15 L10 20",
        ["eraser"] = "M3 15 L14 4 Q15 3 16 4 L21 9 Q22 10 21 11 L12 20 H8 Z M8 10 L16 18 M13 20 H22",
        ["text"] = "M4 7 V4 H20 V7 M12 4 V20 M8 20 H16",
        ["image"] = "M4 4 H20 V20 H4 Z M4 17 L9 12 L13 16 L16 13 L20 17 M17 9 A1.5 1.5 0 1 1 14 9 A1.5 1.5 0 1 1 17 9",
        ["link"] = "M10 13 L14 9 M11 7 L14 4 A4 4 0 0 1 20 10 L17 13 M13 17 L10 20 A4 4 0 0 1 4 14 L7 11",
        ["shape"] = "M3 4 H14 V15 H3 Z M20 11 A5 5 0 1 1 13 18", ["down"] = "M6 9 L12 15 L18 9",
        ["rail"] = "M4 4 H20 V20 H4 Z M9 4 V20", ["spark"] = "M12 2 L14 9 L21 12 L14 15 L12 22 L10 15 L3 12 L10 9 Z"
    }.ToDictionary(pair => pair.Key, pair => { Geometry geometry = Geometry.Parse(pair.Value); geometry.Freeze(); return geometry; });

    public static FrameworkElement Draw(string name, Brush brush, double size = 18) => new Viewbox
    {
        Width = size, Height = size, Stretch = Stretch.Uniform,
        Child = new Canvas
        {
            Width = 24, Height = 24,
            Children = { new System.Windows.Shapes.Path { Data = Paths[name], Stroke = brush, StrokeThickness = 1.65,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round } }
        }
    };
}
