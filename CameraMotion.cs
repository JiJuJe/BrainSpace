using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    private Viewport? motionFrom, motionTo;
    private long motionStart;
    private double motionDuration;
    private Vector cameraTrail;
    private double zoomTrail;
    public event Action? ZoomProgress;

    private void AnimateTo(Viewport destination, double milliseconds, bool animate, Viewport? start = null)
    {
        if (!animate || !IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            StopMotion();
            SetView(destination);
            return;
        }
        if (destination == view && motionTo is null) return;
        if (motionTo is null) CompositionTarget.Rendering += OnCameraFrame;
        motionFrom = start ?? view;
        motionTo = destination;
        motionStart = Stopwatch.GetTimestamp();
        motionDuration = milliseconds;
    }

    public void StopMotion(bool finish = false)
    {
        if (motionTo is not { } target) return;
        CompositionTarget.Rendering -= OnCameraFrame;
        motionTo = motionFrom = null;
        cameraTrail = new();
        zoomTrail = 0;
        SetView(finish ? target : view);
    }

    private void OnCameraFrame(object? sender, EventArgs args)
    {
        double progress = Math.Clamp(Stopwatch.GetElapsedTime(motionStart).TotalMilliseconds / motionDuration, 0, 1);
        AdvanceCamera(progress);
    }

    private void AdvanceCamera(double progress)
    {
        if (motionFrom is not { } start || motionTo is not { } end) return;
        if (progress >= 1) { StopMotion(finish: true); return; }
        double ease = 1 - Math.Pow(1 - progress, 3);
        var next = new Viewport(start.X + (end.X - start.X) * ease, start.Y + (end.Y - start.Y) * ease,
            start.Zoom + (end.Zoom - start.Zoom) * ease);
        double ratio = view.Zoom / next.Zoom;
        zoomTrail = Math.Clamp(ratio - 1, -.045, .045);
        var lag = new Vector(view.X - next.X * ratio, view.Y - next.Y * ratio);
        cameraTrail = ClampLength(lag * .45, 9);
        bool zoomed = next.Zoom != view.Zoom;
        SetView(next, notify: false);
        if (zoomed) ZoomProgress?.Invoke();
    }

    private static Vector ClampLength(Vector vector, double maximum) =>
        vector.LengthSquared <= maximum * maximum ? vector : vector * (maximum / vector.Length);
}
