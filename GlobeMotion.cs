using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BrainSpace;

internal sealed partial class WorkspaceCanvas
{
    private const double SpinDamping = 7.5;
    private const double MaxSpinSpeed = 320;
    private const double MaxSpinSeconds = .65;
    private const double ReleaseWindow = .09;
    private const double StopSpinSpeed = 7;

    private sealed record GlobeSpin(Quaternion Start, Vector3D Axis, double Speed, double Duration, long Timestamp);
    private GlobeSpin? globeSpin;
    private Point globePointer;
    private long globeDragSample, globeLastMovement;
    private Vector3D globeDragVelocity, globePendingRotation;

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
        Vector3D rotation = DragGlobe(globePointer, point);
        globePointer = point;
        if (rotation.LengthSquared == 0) return;
        globeLastMovement = timestamp;
        globePendingRotation += rotation;

        double elapsed = Stopwatch.GetElapsedTime(globeDragSample, timestamp).TotalSeconds;
        if (elapsed < .001) return;
        Vector3D instant = globePendingRotation / elapsed;
        if (instant.Length > MaxSpinSpeed) instant *= MaxSpinSpeed / instant.Length;
        double smoothing = globeDragVelocity.LengthSquared == 0 ? 1 : 1 - Math.Exp(-elapsed / .034);
        globeDragVelocity += (instant - globeDragVelocity) * smoothing;
        globeDragSample = timestamp;
        globePendingRotation = new();
    }

    private bool StartGlobeSpin(long release, bool animate)
    {
        if (!animate || !view.WholeWorkspace) return false;
        double age = Math.Max(0, Stopwatch.GetElapsedTime(globeLastMovement, release).TotalSeconds);
        if (age >= ReleaseWindow) return false;

        double speed = globeDragVelocity.Length * (1 - age / ReleaseWindow);
        double launchSpeed = 52 / Math.Sqrt(view.GlobeMagnification);
        if (speed < launchSpeed) return false;

        double duration = Math.Min(MaxSpinSeconds, Math.Log(speed / StopSpinSpeed) / SpinDamping);
        Vector3D axis = globeDragVelocity / globeDragVelocity.Length;
        globeSpin = new(RotationOf(view.Rotation), axis, speed, duration, release);
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
        double angle = (spin.Speed / SpinDamping) * (1 - Math.Exp(-SpinDamping * time));
        SetGlobeRotation(new Quaternion(spin.Axis, angle) * spin.Start, notify: false);
        if (time >= spin.Duration) StopGlobeSpin();
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
