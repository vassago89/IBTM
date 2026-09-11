using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public sealed record MotionPosition(double? X, double? Y, double? Z);

public sealed class MotionStatus : INotifyPropertyChanged
{
    private Task? _monitoring;
    private readonly TaskCompletionSource _firstMonitorRead = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

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

    public Task MonitoringCompletion
    {
        get
        {
            return _monitoring ?? Task.CompletedTask;
        }
    }

    // Startup awaits the first sample; MonitoringCompletion owns the lifetime of the loop.
    public Task StartMonitoringAsync(
        CancellationToken lifetime,
        Action refreshed,
        Action<MotionAxis, Exception> reportError)
    {
        if (Feedback is not IMotionDiagnostics)
            return Task.CompletedTask;
        if (_monitoring is not null)
            return _firstMonitorRead.Task;
        _monitoring = Task.Run(() => MonitorAsync(lifetime, refreshed, reportError));
        return _firstMonitorRead.Task;
    }

    private async Task MonitorAsync(
        CancellationToken lifetime,
        Action refreshed,
        Action<MotionAxis, Exception> reportError)
    {
        try
        {
            while (true)
            {
                lifetime.ThrowIfCancellationRequested();
                RefreshMonitorFeedback(reportError);
                refreshed();
                _firstMonitorRead.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            _firstMonitorRead.TrySetCanceled(lifetime);
        }
        catch (Exception error)
        {
            foreach (var (axis, status) in MonitorAxes)
            {
                status.Invalidate(error);
                reportError(axis, error);
            }

            PropertyChanged?.Invoke(this, new(nameof(IsMoving)));
            PropertyChanged?.Invoke(this, new(nameof(Position)));
            refreshed();
            _firstMonitorRead.TrySetException(error);
            throw;
        }
    }

    public void RefreshMonitorFeedback(Action<MotionAxis, Exception>? reportError = null)
    {
        if (Feedback is not IMotionDiagnostics diagnostics)
            return;
        var wasMoving = IsMoving;
        var previousPosition = Position;
        foreach (var (axis, status) in MonitorAxes)
        {
            var previous = status.Snapshot.ReadError?.Message;
            status.Refresh(diagnostics, axis);
            if (status.Snapshot.ReadError is { } error && error.Message != previous)
                reportError?.Invoke(axis, error);
        }

        if (wasMoving != IsMoving)
            PropertyChanged?.Invoke(this, new(nameof(IsMoving)));
        if (previousPosition != Position)
            PropertyChanged?.Invoke(this, new(nameof(Position)));
    }

    public bool XyHomed
    {
        get
        {
            return Axes[MotionAxis.X].State is { Homed: true }
                && (!Feedback.HasY || Axes[MotionAxis.Y].State is { Homed: true });
        }
    }

    // Display only; motion commands read Feedback again when they execute.
    public bool IsAtZ(double z)
    {
        return !Feedback.HasZ
            || Axes[MotionAxis.Z].State is { Homed: true }
            && Position.Z is { } current
            && Math.Abs(current - z) <= MotionService.PositionToleranceMillimeters;
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
