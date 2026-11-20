using System.Windows;
using System.Windows.Media;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    private const double StartingRadius = 3600;
    private const double RadiusStep = 900;
    private const double MaxGlobeMagnification = 4;
    private double workspaceRadius = StartingRadius;

    private Viewport FullWorkspaceView
    {
        get
        {
            double diameter = Math.Max(1, Math.Min(ActualWidth - 64, ActualHeight - 100));
            double zoom = Math.Min(1, diameter / (2 * workspaceRadius));
            return new(0, 0, zoom, WholeWorkspace: true, Rotation: view.Rotation);
        }
    }

    public void ShowFullWorkspace() => ShowFullWorkspace(animate: true);

    private void ShowFullWorkspace(bool animate)
    {
        CancelGesture();
        Viewport target = FullWorkspaceView;
        if (motionTo != target) AnimateTo(target, 205, animate);
    }

    private Viewport ConstrainView(Viewport viewport)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return viewport;
        Viewport whole = FullWorkspaceView;
        if (viewport.WholeWorkspace)
        {
            double scale = double.IsFinite(viewport.GlobeMagnification) ? Math.Clamp(viewport.GlobeMagnification, 1, MaxGlobeMagnification) : 1;
            double zoom = whole.Zoom * scale;
            Vector shift = ClampLength(new(viewport.X, viewport.Y), workspaceRadius * (zoom - whole.Zoom));
            return viewport with { X = shift.X, Y = shift.Y, Zoom = zoom, GlobeMagnification = scale };
        }
        double flatZoom = Math.Max(viewport.Zoom, whole.Zoom * 1.001);
        var current = new Vector(viewport.X, viewport.Y);
        Vector offset = ClampLength(current, workspaceRadius * flatZoom);
        if (offset == current && flatZoom == viewport.Zoom) return viewport;
        return viewport with { X = offset.X, Y = offset.Y, Zoom = flatZoom };
    }

    private void FitAfterWorkspaceChange()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (motionTo?.WholeWorkspace == true) StopMotion(finish: true);
        Viewport fitted = ConstrainView(view);
        if (view != fitted) SetView(fitted);
    }

    private void UpdateWorkspaceRadius(Content data)
    {
        double extentSquared = 0, globeExtentSquared = 0;
        if (board == Guid.Empty)
        {
            foreach (Brain brain in data.Brains.Values) Include(BrainLayout.Bounds(brain, home: true), brain);
        }
        else
        {
            foreach (SceneObject obj in objects) Include(obj.Bounds, obj.Brain);
        }
        double extent = Math.Sqrt(extentSquared);
        workspaceRadius = RadiusFor(extent);
        globeMapRadius = Math.Clamp(Math.Sqrt(globeExtentSquared) * 1.24, 850, workspaceRadius);
        globeAnchors = [];
        globeLabelSize = default;
        FitAfterWorkspaceChange();

        void Include(Rect bounds, Brain? brain)
        {
            double corner = FarCornerSquared(bounds);
            extentSquared = Math.Max(extentSquared, corner);
            if (GlobePositionFor(brain) is null) globeExtentSquared = Math.Max(globeExtentSquared, corner);
        }
    }

    private static double RadiusFor(double extent) => Math.Max(StartingRadius,
        Math.Ceiling((extent + Math.Max(430, extent * .065)) / RadiusStep) * RadiusStep);

    private static double FarCornerSquared(Rect bounds)
    {
        double x = Math.Max(Math.Abs(bounds.Left), Math.Abs(bounds.Right));
        double y = Math.Max(Math.Abs(bounds.Top), Math.Abs(bounds.Bottom));
        return x * x + y * y;
    }

    private bool InsideWorkspace(Point point) => point.X * point.X + point.Y * point.Y <= workspaceRadius * workspaceRadius;

    private double DisplayRadius(Item? draft)
    {
        double extentSquared = 0;
        if (gesture is Gesture.Move or Gesture.Resize)
            foreach (Guid id in Selection)
                if (byId.TryGetValue(id, out SceneObject? obj)) extentSquared = Math.Max(extentSquared, FarCornerSquared(DisplayBounds(obj)));
        if (draft is not null) extentSquared = Math.Max(extentSquared, FarCornerSquared(new(draft.X, draft.Y, draft.Width, draft.Height)));
        if (extentSquared < (workspaceRadius - 24) * (workspaceRadius - 24)) return workspaceRadius;
        return Math.Max(workspaceRadius, RadiusFor(Math.Sqrt(extentSquared)));
    }

    private EllipseGeometry? DrawWorkspaceBackground(DrawingContext drawing, Rect screen, Item? draft)
    {
        Point origin = ToScreen(new());
        double radius = DisplayRadius(draft) * view.Zoom;
        double x = Math.Max(Math.Abs(origin.X), Math.Abs(screen.Right - origin.X));
        double y = Math.Max(Math.Abs(origin.Y), Math.Abs(screen.Bottom - origin.Y));
        if (x * x + y * y <= radius * radius)
        {
            drawing.DrawRectangle(theme.Canvas, null, screen);
            return null;
        }

        drawing.DrawRectangle(theme.CanvasOutside, null, screen);
        var boundary = new EllipseGeometry(origin, radius, radius);
        boundary.Freeze();
        drawing.DrawGeometry(theme.Canvas, null, boundary);
        drawing.PushClip(boundary);
        return boundary;
    }
}
