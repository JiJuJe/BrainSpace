using System.Windows;
using System.Windows.Media;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    private const double OverviewZoom = .62;
    private readonly record struct LabelArtwork(DrawingGroup Drawing, double Width, double Height);
    private readonly record struct OverviewLabel(SceneObject Object, Rect Bounds, LabelArtwork Artwork, double TextScale);
    private readonly Dictionary<Guid, Rect> overviewBoxes = [];
    private readonly List<OverviewLabel> overviewLabels = [];
    private readonly Dictionary<Guid, LabelArtwork> overviewTextCache = [];
    private readonly List<Point> crowdedLabels = [];
    private double overviewDpi;
    private bool[] occupied = [];
    private const double LabelCell = 8;
    private const double OverviewOverscan = 200;
    private DrawingGroup? overviewDrawing;
    private Viewport overviewCamera = new();
    private Size overviewSize;
    private Guid overviewHover;
    private Guid? overviewConnecting;
    private readonly HashSet<Guid> overviewSelection = [];
    private Matrix overviewProjection = Matrix.Identity;

    private Rect ScreenBounds(Rect world) => new(ToScreen(world.TopLeft), ToScreen(world.BottomRight));

    private void ArrangeOverview(Rect screen)
    {
        bool dense = drawOrder.Count > 160;
        crowdedLabels.Clear();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != overviewDpi) { overviewTextCache.Clear(); overviewDpi = dpi; }
        int columns = Math.Max(1, (int)Math.Ceiling(screen.Width / LabelCell));
        int rows = Math.Max(1, (int)Math.Ceiling(screen.Height / LabelCell));
        if (dense)
        {
            if (occupied.Length < columns * rows) occupied = new bool[columns * rows];
            else Array.Clear(occupied, 0, columns * rows);
        }
        foreach (SceneObject obj in drawOrder)
            if (Selection.Contains(obj.Id) || obj.Id == hovered) Place(obj, true);
        foreach (SceneObject obj in drawOrder)
            if (!Selection.Contains(obj.Id) && obj.Id != hovered) Place(obj, false);

        void Place(SceneObject obj, bool active)
        {
            if (obj.Title.Length == 0 || erased.Contains(obj.Id)) return;
            Rect projected = ScreenBounds(DisplayBounds(obj));
            Point center = new(projected.X + projected.Width / 2, projected.Y + projected.Height / 2);
            if (dense && !active && (!screen.Contains(center) || Occupied(new Rect(center.X - 11, center.Y - 4, 22, 8), mark: false)))
            {
                if (screen.Contains(center)) crowdedLabels.Add(center);
                return;
            }
            bool root = obj.Brain is { Parent: var parent } && parent == Guid.Empty;
            double size = obj.Brain is not null ? root ? 27 : 19 : obj.Item?.FontSize ?? 19;
            double minimum = (root ? 5.5 : 4.7) + 6 * view.Zoom;
            double scale = Math.Max(view.Zoom, minimum / size);
            Brush foreground = obj.Item?.Kind == ItemKind.Link ? theme.Accent : theme.Text;
            if (!overviewTextCache.TryGetValue(obj.Id, out LabelArtwork text))
            {
                double maxWidth = Math.Clamp(obj.Bounds.Width - (obj.Brain is null ? 36 : 40), 45, 470);
                FormattedText layout = Text(obj.Title, size, maxWidth, foreground, root || obj.Item?.Bold == true, obj.Item?.Italic == true);
                var artwork = new DrawingGroup();
                using (DrawingContext context = artwork.Open()) context.DrawText(layout, new());
                artwork.Freeze();
                text = new(artwork, layout.Width, layout.Height);
                if (overviewTextCache.Count < 8192) overviewTextCache.Add(obj.Id, text);
            }
            double height = Math.Max(projected.Height, text.Height * scale + 2);
            double width = Math.Max(projected.Width, text.Width * scale + Math.Max(4, 20 * view.Zoom));
            var bounds = new Rect(center.X - width / 2, center.Y - height / 2, width, height);
            if (!screen.IntersectsWith(bounds)) return;
            if (dense && !active && Occupied(bounds, mark: false)) { crowdedLabels.Add(center); return; }
            if (dense) Occupied(bounds, mark: true);
            overviewBoxes.Add(obj.Id, bounds);
            overviewLabels.Add(new(obj, bounds, text, scale));
        }

        bool Occupied(Rect bounds, bool mark)
        {
            int left = Math.Clamp((int)Math.Floor((bounds.Left - screen.Left - 2) / LabelCell), 0, columns - 1);
            int right = Math.Clamp((int)Math.Floor((bounds.Right - screen.Left + 2) / LabelCell), 0, columns - 1);
            int top = Math.Clamp((int)Math.Floor((bounds.Top - screen.Top) / LabelCell), 0, rows - 1);
            int bottom = Math.Clamp((int)Math.Floor((bounds.Bottom - screen.Top) / LabelCell), 0, rows - 1);
            for (int row = top; row <= bottom; row++)
                for (int index = row * columns + left, end = row * columns + right; index <= end; index++)
                {
                    if (mark) occupied[index] = true;
                    else if (occupied[index]) return true;
                }
            return false;
        }
    }

    private void BuildCachedOverview(DrawingContext drawing, Rect screen, Rect visible)
    {
        var overview = new DrawingGroup();
        using (DrawingContext context = overview.Open()) DrawOverview(context, visible, trailsEnabled: false);
        overview.Freeze();
        overviewDrawing = overview;
        overviewCamera = view;
        overviewSize = screen.Size;
        overviewHover = hovered;
        overviewConnecting = Connecting;
        overviewSelection.Clear();
        overviewSelection.UnionWith(Selection);
        drawing.DrawDrawing(overview);
    }

    private bool DrawCachedOverview(DrawingContext drawing, Rect screen)
    {
        if (overviewDrawing is null || view.Zoom >= OverviewZoom || VisualTreeHelper.GetDpi(this).PixelsPerDip != overviewDpi ||
            gesture is not (Gesture.None or Gesture.Pan) || overviewSize != screen.Size || overviewHover != hovered ||
            overviewConnecting != Connecting || !overviewSelection.SetEquals(Selection)) return false;
        double scale = view.Zoom / overviewCamera.Zoom;
        if (Math.Abs(1 - scale) > .048) return false;
        var shift = new Vector(view.X - overviewCamera.X * scale, view.Y - overviewCamera.Y * scale);
        if (Math.Max(Math.Abs(shift.X), Math.Abs(shift.Y)) > OverviewOverscan - 38) return false;
        overviewProjection = new(scale, 0, 0, scale, Center.X * (1 - scale) + shift.X, Center.Y * (1 - scale) + shift.Y);
        drawing.PushTransform(new MatrixTransform(overviewProjection));
        drawing.DrawDrawing(overviewDrawing);
        drawing.Pop();
        return true;
    }

    private void DrawOverview(DrawingContext drawing, Rect visible, bool trailsEnabled)
    {
        drawing.PushTransform(new MatrixTransform(view.Zoom, 0, 0, view.Zoom, Center.X + view.X, Center.Y + view.Y));
        DrawConnections(drawing, visible, overview: true);
        drawing.Pop();
        if (crowdedLabels.Count > 0)
        {
            var dots = new StreamGeometry();
            using (StreamGeometryContext geometry = dots.Open())
                foreach (Point point in crowdedLabels)
                {
                    geometry.BeginFigure(new(point.X - 1.2, point.Y - 1.2), true, true);
                    geometry.LineTo(new(point.X + 1.2, point.Y - 1.2), false, false);
                    geometry.LineTo(new(point.X + 1.2, point.Y + 1.2), false, false);
                    geometry.LineTo(new(point.X - 1.2, point.Y + 1.2), false, false);
                }
            dots.Freeze();
            drawing.DrawGeometry(theme.Edge, null, dots);
        }
        int trails = 18;
        if (trailsEnabled)
            foreach (OverviewLabel label in overviewLabels)
                if (trails > 0 && DrawMotionTrail(drawing, label.Bounds, world: false)) trails--;
        foreach (OverviewLabel label in overviewLabels)
            if (!Selection.Contains(label.Object.Id) && hovered != label.Object.Id) DrawLabel(label);
        foreach (OverviewLabel label in overviewLabels)
            if (Selection.Contains(label.Object.Id) || hovered == label.Object.Id) DrawLabel(label);
        foreach (OverviewLabel label in overviewLabels)
            if (!Selection.Contains(label.Object.Id) && hovered != label.Object.Id) DrawTitle(label);
        foreach (OverviewLabel label in overviewLabels)
            if (Selection.Contains(label.Object.Id) || hovered == label.Object.Id) DrawTitle(label);
        if (gesture == Gesture.None && view.Zoom <= .32 && hovered != Guid.Empty && byId.TryGetValue(hovered, out SceneObject? hoveredNode) && hoveredNode.Title.Length > 0)
        {
            Rect anchor = ScreenBounds(hoveredNode.Bounds);
            FormattedText name = Text(hoveredNode.Title, 13, 320, theme.Text, false, false);
            double width = name.Width + 16;
            double height = name.Height + 9;
            double x = Math.Clamp(anchor.X + (anchor.Width - width) / 2, 3, Math.Max(3, ActualWidth - width - 3));
            double y = anchor.Bottom + 10;
            if (y + height > ActualHeight - 3) y = anchor.Top - height - 10;
            drawing.DrawRoundedRectangle(theme.Surface, Stroke(theme.Border, 1), new(x, y, width, height), 6, 6);
            drawing.DrawText(name, new(x + 8, y + 4));
        }

        void DrawLabel(OverviewLabel label)
        {
            SceneObject obj = label.Object;
            bool active = Selection.Contains(obj.Id) || hovered == obj.Id || Connecting == obj.Id;
            bool root = obj.Brain is { Parent: var parent } && parent == Guid.Empty;
            Rect bounds = label.Bounds;
            double radius = obj.Brain is null ? 6 : bounds.Height / 2;
            Brush border = active || root ? theme.Accent : obj.Brain is null ? theme.Border : theme.Edge;
            drawing.DrawRoundedRectangle(active ? theme.Active : theme.Node, Stroke(border, active ? 1.4 : .85), bounds, radius, radius);
        }

        void DrawTitle(OverviewLabel label)
        {
            Rect bounds = label.Bounds;
            double scale = label.TextScale;
            double x = bounds.X + (bounds.Width - label.Artwork.Width * scale) / 2;
            double y = bounds.Y + (bounds.Height - label.Artwork.Height * scale) / 2;
            drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
            drawing.DrawDrawing(label.Artwork.Drawing);
            drawing.Pop();
        }
    }

    private bool DrawMotionTrail(DrawingContext drawing, Rect screen, bool world)
    {
        if (cameraTrail.LengthSquared < .25 && Math.Abs(zoomTrail) < .002) return false;
        var center = new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
        Vector shift = ClampLength(cameraTrail + (center - Center) * zoomTrail, 10);
        if (shift.LengthSquared < 2.25) return false;
        for (int layer = 1; layer <= 2; layer++)
        {
            Rect ghost = screen;
            ghost.Offset(shift * (layer * .52));
            double scale = world ? view.Zoom : 1;
            if (world) ghost = new(ToWorld(ghost.TopLeft), ToWorld(ghost.BottomRight));
            drawing.PushOpacity(layer == 1 ? .24 : .115);
            drawing.DrawRoundedRectangle(null, Stroke(theme.Accent, 1.8 / scale), ghost, ghost.Height / 2, ghost.Height / 2);
            drawing.Pop();
        }
        return true;
    }

    private SceneObject? HitOverview(Point screen)
    {
        if (!overviewProjection.IsIdentity)
        {
            Matrix inverse = overviewProjection;
            inverse.Invert();
            screen = inverse.Transform(screen);
        }
        SceneObject? nearest = null;
        double distance = double.PositiveInfinity;
        for (int i = overviewLabels.Count - 1; i >= 0; i--)
        {
            OverviewLabel label = overviewLabels[i];
            if (!label.Bounds.Contains(screen)) continue;
            var center = new Point(label.Bounds.X + label.Bounds.Width / 2, label.Bounds.Y + label.Bounds.Height / 2);
            double candidate = (screen - center).LengthSquared;
            if (candidate >= distance) continue;
            nearest = label.Object;
            distance = candidate;
        }
        return nearest;
    }
}
