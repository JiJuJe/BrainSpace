using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    private static readonly GlobeOrientation UprightGlobe = new(0, 0, 0, 1);
    private static readonly Point3D[][] GlobeGrid = CreateGlobeGrid();
    private const int GlobeLabelLimit = 54;
    private double globeMapRadius = 850;
    private double GlobeRadius => workspaceRadius * view.Zoom;
    private Point GlobeCenter => Center + new Vector(view.X, view.Y);
    private Point3D[] globeAnchors = [], globeProjected = [];
    private bool[] globeDotCells = [];
    private readonly List<(SceneObject Object, Rect Bounds)> globeLabels = [];
    private Viewport? globeLabelView;
    private Size globeLabelSize;
    private Brush? globeFill, globeGridBrush;
    private Point globePointer;
    private int globeClickCount;
    private MouseButton globeButton;

    private static Quaternion RotationOf(GlobeOrientation? value)
    {
        if (value is not { } rotation) return Quaternion.Identity;
        double length = rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z + rotation.W * rotation.W;
        if (!double.IsFinite(length) || length < 1e-12) return Quaternion.Identity;
        var result = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
        result.Normalize();
        return result;
    }

    private Matrix3D GlobeMatrix()
    {
        Matrix3D matrix = Matrix3D.Identity;
        matrix.Rotate(RotationOf(view.Rotation));
        return matrix;
    }

    private void RotateGlobe(Vector3D axis, double degrees, bool notify)
    {
        if (axis.LengthSquared < 1e-12 || degrees == 0) return;
        Quaternion rotation = new Quaternion(axis, degrees) * RotationOf(view.Rotation);
        rotation.Normalize();
        SetView(view with { Rotation = new(rotation.X, rotation.Y, rotation.Z, rotation.W) }, notify);
    }

    private void DragGlobe(Point from, Point to)
    {
        Vector3D first = Trackball(from), second = Trackball(to);
        Vector3D axis = Vector3D.CrossProduct(first, second);
        double degrees = Math.Atan2(axis.Length, Vector3D.DotProduct(first, second)) * 180 / Math.PI;
        RotateGlobe(axis, degrees, notify: false);
    }

    private Vector3D Trackball(Point point)
    {
        Vector position = (point - GlobeCenter) / Math.Max(1, GlobeRadius);
        if (position.LengthSquared >= 1) return new(position.X / position.Length, position.Y / position.Length, 0);
        return new(position.X, position.Y, Math.Sqrt(1 - position.LengthSquared));
    }

    private Point? GlobeWorldAt(Point point)
    {
        if ((point - GlobeCenter).LengthSquared > GlobeRadius * GlobeRadius) return null;
        Matrix3D inverse = GlobeMatrix();
        inverse.Invert();
        Vector3D surface = inverse.Transform(Trackball(point));
        return surface.Z >= 0 ? new Point(surface.X * globeMapRadius, surface.Y * globeMapRadius) : null;
    }

    private void EnsureGlobeAnchors()
    {
        if (globeAnchors.Length == objects.Count) return;
        globeAnchors = new Point3D[objects.Count];
        globeProjected = new Point3D[objects.Count];
        foreach (SceneObject obj in objects)
        {
            double x = (obj.Bounds.X + obj.Bounds.Width / 2) / globeMapRadius;
            double y = (obj.Bounds.Y + obj.Bounds.Height / 2) / globeMapRadius;
            globeAnchors[obj.Order] = new(x, y, Math.Sqrt(Math.Max(0, 1 - x * x - y * y)));
        }
    }

    private void DrawGlobe(DrawingContext drawing, Rect screen)
    {
        double radius = GlobeRadius;
        Point center = GlobeCenter;
        drawing.DrawRectangle(theme.CanvasOutside, null, screen);
        if (radius <= 1) return;
        EnsureGlobeAnchors();
        Matrix3D matrix = GlobeMatrix();
        for (int i = 0; i < globeAnchors.Length; i++) globeProjected[i] = matrix.Transform(globeAnchors[i]);
        globeLabels.Clear();
        globeLabelView = view;
        globeLabelSize = RenderSize;

        if (globeFill is null) CreateGlobeBrushes();
        var outline = new EllipseGeometry(center, radius, radius);
        drawing.DrawGeometry(globeFill, null, outline);
        drawing.PushClip(outline);

        var grid = new StreamGeometry();
        using (StreamGeometryContext lines = grid.Open())
        {
            foreach (Point3D[] ring in GlobeGrid)
            {
                Point3D previous = matrix.Transform(ring[0]);
                for (int i = 1; i < ring.Length; i++)
                {
                    Point3D next = matrix.Transform(ring[i]);
                    AddSegment(lines, previous, next, center, radius);
                    previous = next;
                }
            }
        }
        drawing.DrawGeometry(null, Stroke(globeGridBrush!, .8), grid);

        var links = new StreamGeometry();
        int edgeStride = Math.Max(1, (objects.Count + 399) / 400);
        using (StreamGeometryContext lines = links.Open())
            foreach (SceneObject obj in objects)
                if (adjacency.TryGetValue(obj.Id, out List<Curve>? curves))
                    foreach (Curve edge in curves)
                        if (ReferenceEquals(edge.From, obj))
                        {
                            if (edge.To.Order % edgeStride != 0 && edge.To.Id != hovered && !Selection.Contains(edge.To.Id)) continue;
                            Point3D from = globeProjected[obj.Order], to = globeProjected[edge.To.Order];
                            if (from.Z <= 0 && to.Z <= 0) continue;
                            var mid = new Vector3D(from.X + to.X, from.Y + to.Y, from.Z + to.Z);
                            mid.Normalize();
                            var midpoint = new Point3D(mid.X, mid.Y, mid.Z);
                            AddSegment(lines, from, midpoint, center, radius);
                            AddSegment(lines, midpoint, to, center, radius);
                        }
        drawing.DrawGeometry(null, Stroke(theme.Edge, 1), links);

        bool dense = objects.Count > 320;
        const int cellSize = 8;
        int columns = (int)Math.Ceiling(2 * radius / cellSize) + 1;
        if (dense)
        {
            int cells = columns * columns;
            if (globeDotCells.Length < cells) globeDotCells = new bool[cells];
            else Array.Clear(globeDotCells, 0, cells);
        }
        double labelScale = Math.Min(1.65, Math.Sqrt(view.GlobeMagnification));
        foreach (SceneObject obj in objects)
        {
            Point3D point = globeProjected[obj.Order];
            if (point.Z <= 0) continue;
            Point at = GlobeScreen(point, center, radius);
            if (!screen.Contains(at)) continue;
            if (dense)
            {
                int x = (int)((point.X + 1) * radius / cellSize);
                int y = (int)((point.Y + 1) * radius / cellSize);
                int cell = y * columns + x;
                if (globeDotCells[cell]) continue;
                globeDotCells[cell] = true;
            }
            double size = (.9 + point.Z * 1.15) * labelScale;
            drawing.DrawEllipse(theme.Muted, null, at, size, size);
        }

        int labelsMeasured = 0;
        if (hovered != Guid.Empty && byId.TryGetValue(hovered, out SceneObject? underPointer)) PlaceLabel(underPointer, true);
        foreach (Guid id in Selection)
            if (id != hovered && byId.TryGetValue(id, out SceneObject? selected)) PlaceLabel(selected, true);
        foreach (SceneObject obj in objects)
        {
            if (globeLabels.Count >= GlobeLabelLimit || labelsMeasured >= (dense ? 48 : 115)) break;
            if (obj.Id != hovered && !Selection.Contains(obj.Id)) PlaceLabel(obj, false);
        }
        for (int i = globeLabels.Count - 1; i >= 0; i--)
        {
            (SceneObject obj, Rect bounds) = globeLabels[i];
            bool active = obj.Id == hovered || Selection.Contains(obj.Id);
            FormattedText label = GlobeText(obj);
            drawing.DrawRoundedRectangle(active ? theme.Active : theme.Node, Stroke(active ? theme.Accent : theme.Edge, active ? 1.2 : .8), bounds, 8, 8);
            Point at = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            if (labelScale != 1) drawing.PushTransform(new ScaleTransform(labelScale, labelScale, at.X, at.Y));
            drawing.DrawText(label, new(at.X - label.Width / 2, at.Y - label.Height / 2));
            if (labelScale != 1) drawing.Pop();
        }
        drawing.Pop();
        drawing.DrawGeometry(null, Stroke(theme.Edge, 1.2), outline);

        if (radius >= 105 && center.Y + radius + 30 < screen.Bottom)
        {
            FormattedText hint = Text("Drag to rotate  ·  Scroll to zoom  ·  F for map", 11, 265, theme.Muted, false, false);
            drawing.DrawText(hint, new(center.X - hint.Width / 2, center.Y + radius + 13));
        }

        void PlaceLabel(SceneObject obj, bool active)
        {
            if (globeLabels.Count >= GlobeLabelLimit || obj.Title.Length == 0) return;
            Point3D point = globeProjected[obj.Order];
            if (point.Z < (active ? .01 : .13)) return;
            Point at = GlobeScreen(point, center, radius);
            if (!screen.Contains(at)) return;
            foreach (var existing in globeLabels)
                if (existing.Bounds.Contains(at)) return;
            labelsMeasured++;
            FormattedText label = GlobeText(obj);
            double width = (label.Width + 14) * labelScale, height = Math.Max(19, label.Height + 5) * labelScale;
            var bounds = new Rect(at.X - width / 2, at.Y - height / 2, width, height);
            if (!active && ((bounds.TopLeft - center).LengthSquared >= radius * radius ||
                (bounds.TopRight - center).LengthSquared >= radius * radius ||
                (bounds.BottomLeft - center).LengthSquared >= radius * radius ||
                (bounds.BottomRight - center).LengthSquared >= radius * radius)) return;
            foreach (var existing in globeLabels)
                if (existing.Bounds.IntersectsWith(bounds)) return;
            globeLabels.Add((obj, bounds));
        }
    }

    private FormattedText GlobeText(SceneObject obj)
    {
        bool root = obj.Brain is { Parent: var parent } && parent == Guid.Empty;
        return Text(obj.Title, root ? 12 : 10.5, 112, obj.Item?.Kind == ItemKind.Link ? theme.Accent : theme.Text, root, false);
    }

    private SceneObject? HitGlobe(Point point)
    {
        double radius = GlobeRadius;
        Point center = GlobeCenter;
        if ((point - center).LengthSquared > radius * radius) return null;
        if (globeLabelView == view && globeLabelSize == RenderSize)
            foreach (var label in globeLabels)
                if (label.Bounds.Contains(point)) return label.Object;
        EnsureGlobeAnchors();
        Matrix3D matrix = GlobeMatrix();
        SceneObject? nearest = null;
        double distance = 8 * 8;
        foreach (SceneObject obj in objects)
        {
            Point3D projected = matrix.Transform(globeAnchors[obj.Order]);
            if (projected.Z <= 0) continue;
            double squared = (GlobeScreen(projected, center, radius) - point).LengthSquared;
            if (squared >= distance) continue;
            nearest = obj;
            distance = squared;
        }
        return nearest;
    }

    private static Point GlobeScreen(Point3D point, Point center, double radius) => new(center.X + point.X * radius, center.Y + point.Y * radius);

    private static void AddSegment(StreamGeometryContext lines, Point3D from, Point3D to, Point center, double radius)
    {
        if (from.Z <= 0 && to.Z <= 0) return;
        if (from.Z < 0) from += (to - from) * (-from.Z / (to.Z - from.Z));
        else if (to.Z < 0) to += (from - to) * (-to.Z / (from.Z - to.Z));
        lines.BeginFigure(GlobeScreen(from, center, radius), isFilled: false, isClosed: false);
        lines.LineTo(GlobeScreen(to, center, radius), isStroked: true, isSmoothJoin: false);
    }

    private static Point3D[][] CreateGlobeGrid()
    {
        var rings = new List<Point3D[]>();
        const int steps = 64;
        for (int latitude = -60; latitude <= 60; latitude += 30)
        {
            double angle = latitude * Math.PI / 180;
            double width = Math.Cos(angle), y = Math.Sin(angle);
            rings.Add(Enumerable.Range(0, steps + 1).Select(i =>
            {
                double turn = i * (2 * Math.PI / steps);
                return new Point3D(Math.Sin(turn) * width, y, Math.Cos(turn) * width);
            }).ToArray());
        }
        for (int longitude = 0; longitude < 180; longitude += 30)
        {
            double angle = longitude * Math.PI / 180;
            rings.Add(Enumerable.Range(0, steps + 1).Select(i =>
            {
                double turn = i * (2 * Math.PI / steps);
                return new Point3D(Math.Sin(angle) * Math.Cos(turn), Math.Sin(turn), Math.Cos(angle) * Math.Cos(turn));
            }).ToArray());
        }
        return rings.ToArray();
    }

    private void CreateGlobeBrushes()
    {
        var fill = new RadialGradientBrush
        {
            Center = new(.40, .36), GradientOrigin = new(.31, .26), RadiusX = .68, RadiusY = .68,
            GradientStops = new()
            {
                new(Mix(theme.Canvas.Color, theme.Accent.Color, .17), 0),
                new(Mix(theme.Canvas.Color, theme.Accent.Color, .05), .63),
                new(Mix(theme.Canvas.Color, theme.CanvasOutside.Color, .78), 1)
            }
        };
        fill.Freeze();
        globeFill = fill;
        var grid = new SolidColorBrush(Mix(theme.Canvas.Color, theme.Edge.Color, .44));
        grid.Freeze();
        globeGridBrush = grid;

        static Color Mix(Color a, Color b, double weight) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * weight), (byte)(a.G + (b.G - a.G) * weight), (byte)(a.B + (b.B - a.B) * weight));
    }
}
