using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    private const double SpinDamping = 8.5;
    private const double MaxSpinSpeed = 245;
    private const double ReleaseWindow = .075;
    private const double StopSpinSpeed = 9;

    private sealed record GlobeSpin(GlobeAngles Start, Vector Velocity, double Duration, long Timestamp);
    private GlobeSpin? globeSpin;
    private Point globePointer;
    private long globeDragSample, globeLastMovement;
    private Vector globeDragVelocity, globePendingRotation;

    private void BeginGlobeDrag(Point point, long timestamp)
    {
        StopGlobeSpin();
        globePointer = point;
        globeLastMovement = globeDragSample = timestamp;
        globeDragVelocity = globePendingRotation = new();
    }

    private void UpdateGlobeDrag(Point point, long timestamp)
    {
        moved |= (point - startScreen).LengthSquared >= 9;
        if (!moved || point == globePointer) return;
        Vector rotation = DragGlobe(globePointer, point);
        globePointer = point;
        if (rotation.LengthSquared == 0) return;
        globeLastMovement = timestamp;
        globePendingRotation += rotation;

        double elapsed = Stopwatch.GetElapsedTime(globeDragSample, timestamp).TotalSeconds;
        if (elapsed < .001) return;
        Vector instant = ClampLength(globePendingRotation / elapsed, MaxSpinSpeed);
        double weight = 1 - Math.Exp(-elapsed / .022);
        globeDragVelocity = new(Blend(globeDragVelocity.X, instant.X), Blend(globeDragVelocity.Y, instant.Y));
        globeDragSample = timestamp;
        globePendingRotation = new();

        double Blend(double previous, double latest) => previous * latest <= 0 ? latest : previous + (latest - previous) * weight;
    }

    private bool StartGlobeSpin(long release, bool animate)
    {
        if (!animate || !view.WholeWorkspace) return false;
        double age = Math.Max(0, Stopwatch.GetElapsedTime(globeLastMovement, release).TotalSeconds);
        if (age >= ReleaseWindow) return false;

        GlobeAngles start = GetGlobeAngles();
        Vector velocity = globeDragVelocity * (1 - age / ReleaseWindow);
        if (Math.Abs(start.Pitch) >= MaxGlobePitch - 1e-6 && start.Pitch * velocity.Y > 0) velocity.Y = 0;
        double speed = velocity.Length;
        double launchSpeed = 52 / Math.Sqrt(view.GlobeMagnification);
        if (speed < launchSpeed) return false;

        double duration = Math.Log(speed / StopSpinSpeed) / SpinDamping;
        globeSpin = new(start, velocity, duration, release);
        CompositionTarget.Rendering += OnGlobeSpinFrame;
        return true;
    }

    private void OnGlobeSpinFrame(object? sender, EventArgs args)
    {
        if (globeSpin is { } spin) AdvanceGlobeSpin(Stopwatch.GetElapsedTime(spin.Timestamp).TotalSeconds);
    }

    private void AdvanceGlobeSpin(double seconds)
    {
        if (globeSpin is not { } spin) return;
        double time = Math.Clamp(seconds, 0, spin.Duration);
        double travel = (1 - Math.Exp(-SpinDamping * time)) / SpinDamping;
        GlobeAngles orientation = spin.Start.Offset(spin.Velocity * travel);
        SetGlobeAngles(orientation, notify: false);
        if (time >= spin.Duration || (spin.Velocity.X == 0 && Math.Abs(orientation.Pitch) == MaxGlobePitch))
            StopGlobeSpin();
    }

    private bool StopGlobeSpin()
    {
        if (globeSpin is null) return false;
        CompositionTarget.Rendering -= OnGlobeSpinFrame;
        globeSpin = null;
        ViewChanged?.Invoke(view);
        return true;
    }
}
