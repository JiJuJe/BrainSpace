using System.Windows;
using System.Windows.Media;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    private const double StartingRadius = 3600;
    private const double RadiusStep = 900;
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
        if (viewport.WholeWorkspace || viewport.Zoom <= whole.Zoom) return whole with { Rotation = viewport.Rotation };
        var current = new Vector(viewport.X, viewport.Y);
        Vector offset = ClampLength(current, workspaceRadius * viewport.Zoom);
        if (offset == current) return viewport;
        return viewport with { X = offset.X, Y = offset.Y };
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
        double extentSquared = 0;
        if (board == Guid.Empty)
        {
            foreach (Brain brain in data.Brains.Values)
                extentSquared = Math.Max(extentSquared, FarCornerSquared(BrainLayout.Bounds(brain, home: true)));
        }
        else
        {
            foreach (SceneObject obj in objects)
                extentSquared = Math.Max(extentSquared, FarCornerSquared(obj.Bounds));
        }
        double extent = Math.Sqrt(extentSquared);
        workspaceRadius = RadiusFor(extent);
        globeMapRadius = Math.Clamp(extent * 1.24, 850, workspaceRadius);
        globeAnchors = [];
        globeLabelSize = default;
        FitAfterWorkspaceChange();
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
