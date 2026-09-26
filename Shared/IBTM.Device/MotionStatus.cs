using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

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

    public bool IsFeedbackAvailable => Axes.Values.All(axis => axis.State is not null);

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

    public void RefreshControlFeedback(bool available = true)
    {
        var wasAvailable = IsFeedbackAvailable;
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
            if (wasAvailable != IsFeedbackAvailable)
                PropertyChanged?.Invoke(this, new(nameof(IsFeedbackAvailable)));
            if (wasMoving != IsMoving)
                PropertyChanged?.Invoke(this, new(nameof(IsMoving)));
            if (wasHomed != XyHomed)
                PropertyChanged?.Invoke(this, new(nameof(XyHomed)));
        }
    }
}
