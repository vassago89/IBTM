using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using IBTM.Core;

namespace IBTM.Device;

public sealed record MotionPosition(double? X, double? Y, double? Z);

public sealed class MotionStatus : INotifyPropertyChanged
{
    public MotionStatus(IMotionFeedback motion)
    {
        Feedback = motion;
        Axes = motion.Axes.ToDictionary(axis => axis, _ => new AxisStatus());
        MonitorAxes = motion.Axes.ToDictionary(axis => axis, _ => new MotionDiagnostics());

    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IMotionFeedback Feedback { get; }
    // Enabled/initialized control feedback; unavailable control invalidates these axes.
    public IReadOnlyDictionary<MotionAxis, AxisStatus> Axes { get; }
    // Independent raw monitoring continues for disabled, servo-off and alarmed axes.
    public IReadOnlyDictionary<MotionAxis, MotionDiagnostics> MonitorAxes { get; }

    public bool XyHomed
    {
        get
        {
            return Axes[MotionAxis.X].State is { Homed: true }
                && (!Feedback.HasY || Axes[MotionAxis.Y].State is { Homed: true });
        }
    }

    public MotionPosition Position
    {
        get
        {
            // The axis snapshots own the coordinates; this is not a second position cache.
            return new(
                MonitorAxes[MotionAxis.X].Snapshot.Position,
                Feedback.HasY ? MonitorAxes[MotionAxis.Y].Snapshot.Position : null,
                Feedback.HasZ ? MonitorAxes[MotionAxis.Z].Snapshot.Position : null);
        }
    }

    public bool IsMoving
    {
        get
        {
            // Observed movement only. Unknown feedback is exposed by the axis status;
            // command admission must still read current hardware feedback.
            return Feedback is IMotionDiagnostics
                ? MonitorAxes.Values.Any(axis => axis.Snapshot.State is { InMotion: true })
                : Axes.Values.Any(axis => axis.State is { InMotion: true });
        }
    }

    // The caller chooses the source explicitly. Cached reads never fall back to the SDK.
    public bool IsReady(bool live)
    {
        return live ? Feedback.IsReady : Axes.Values.All(axis => axis.State is not null);
    }

    public AxisState ReadAxisState(MotionAxis axis, bool live)
    {
        return live ? Feedback.GetAxisState(axis)
            : Axes[axis].State ?? throw new IOException($"Axis {axis} feedback is unavailable.");
    }

    public (double X, double Y, double Z) ReadPosition(bool live)
    {
        if (live)
            return Feedback.GetPosition();
        var position = Position;
        if (position.X is null
            || Feedback.HasY && position.Y is null
            || Feedback.HasZ && position.Z is null)
            throw new IOException("Motion position feedback is unavailable.");
        return (position.X.Value, position.Y ?? 0, position.Z ?? 0);
    }

    public bool IsSettled(bool live, params MotionAxis[] axes)
    {
        return !(live ? Feedback.IsMoving : IsMoving)
            && axes.All(axis => ReadAxisState(axis, live).InPosition);
    }

    public bool IsAt(AxisPosition target, bool live = true)
    {
        if (!IsReady(live)
            || Feedback.Axes.Any(axis => !ReadAxisState(axis, live).Homed)
            || !IsSettled(live, Feedback.Axes.ToArray()))
            return false;
        var current = ReadPosition(live);
        return Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && (!Feedback.HasY || Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters)
            && (!Feedback.HasZ || Math.Abs(current.Z - target.Z) <= MotionService.PositionToleranceMillimeters);
    }

    public void InvalidateFeedback(Exception error)
    {
        foreach (var status in MonitorAxes.Values)
            status.Invalidate(error);
        RefreshControlFeedback(available: false);
        PropertyChanged?.Invoke(this, new(nameof(IsMoving)));
        PropertyChanged?.Invoke(this, new(nameof(Position)));
    }

    public void RefreshMonitorFeedback(Action<MotionAxis, Exception>? reportError = null)
    {
        if (Feedback is not IMotionDiagnostics diagnostics)
            return;
        var wasMoving = IsMoving;
        var previousPosition = Position;
        foreach (var (axis, status) in MonitorAxes)
        {
            var previous = status.Snapshot.ReadError;
            status.Refresh(diagnostics, axis);
            // Report once per failed acquisition period; keep the latest exception in the snapshot.
            if (status.Snapshot.ReadError is { } error && previous is null)
                reportError?.Invoke(axis, error);
        }

        if (wasMoving != IsMoving)
            PropertyChanged?.Invoke(this, new(nameof(IsMoving)));
        if (previousPosition != Position)
            PropertyChanged?.Invoke(this, new(nameof(Position)));
    }

    public bool IsAtZ(double z, bool live = false)
    {
        if (live)
            return !Feedback.HasZ
                || Feedback.GetAxisState(MotionAxis.Z).Homed
                && Math.Abs(Feedback.GetPosition().Z - z) <= MotionService.PositionToleranceMillimeters;
        return !Feedback.HasZ
            || Axes[MotionAxis.Z].State is { Homed: true }
            && Position.Z is { } current
            && Math.Abs(current - z) <= MotionService.PositionToleranceMillimeters;
    }

    public void RefreshControlFeedback(bool available = true)
    {
        var wasHomed = XyHomed;
        var wasMoving = IsMoving;
        try
        {
            // A failed monitor sample already establishes unavailable feedback.
            // Do not query the same disconnected driver again through throwing command getters.
            var readable = MonitorAxes.Values.All(axis => axis.Snapshot.ReadError is null);
            var ready = available && readable && Feedback.IsReady;
            foreach (var (axis, status) in Axes)
            {
                // The monitor owns acquisition. Control displays reuse its sample;
                // actual motion commands still read the hardware before acting.
                AxisState? state = null;
                if (ready)
                {
                    state = Feedback is IMotionDiagnostics
                        ? MonitorAxes[axis].Snapshot.State
                        : Feedback.GetAxisState(axis);
                }

                status.Update(state);
            }
        }
        catch (IOException)
        {
            foreach (var status in Axes.Values)
                status.Update(null);
            throw;
        }
        finally
        {
            if (wasMoving != IsMoving)
                PropertyChanged?.Invoke(this, new(nameof(IsMoving)));
            if (wasHomed != XyHomed)
                PropertyChanged?.Invoke(this, new(nameof(XyHomed)));
        }
    }
}
