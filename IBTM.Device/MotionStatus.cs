using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public sealed record MotionPosition(double X, double Y, double Z);

public sealed class MotionStatus : INotifyPropertyChanged
{
    private MotionPosition _position;
    private bool _isMoving;
    private Task? _monitoring;
    private readonly TaskCompletionSource _firstMonitorRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MotionStatus(IMotionFeedback motion)
    {
        Feedback = motion;
        Axes = motion.Axes.ToDictionary(axis => axis, _ => new AxisStatus());
        MonitorAxes = motion.Axes.ToDictionary(axis => axis, _ => new MotionDiagnostics());
        _position = new(0, 0, 0);
        _isMoving = motion.IsMoving;

        var zRange = motion.GetRange(MotionAxis.Z);
        ZMinimum = zRange?.Minimum ?? 0;
        ZMaximum = zRange?.Maximum ?? 0;

        motion.PositionChanged += OnPositionChanged;
        motion.MovingChanged += OnMovingChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IMotionFeedback Feedback { get; }
    // Enabled/initialized control feedback; unavailable control invalidates these axes.
    public IReadOnlyDictionary<MotionAxis, AxisStatus> Axes { get; }
    // Independent raw monitoring continues for disabled, servo-off and alarmed axes.
    public IReadOnlyDictionary<MotionAxis, MotionDiagnostics> MonitorAxes { get; }
    public Task MonitoringCompletion => _monitoring ?? Task.CompletedTask;

    // Startup awaits the first sample; MonitoringCompletion owns the lifetime of the loop.
    public Task StartMonitoringAsync(CancellationToken lifetime, Action refreshed,
        Action<MotionAxis, Exception> reportError)
    {
        if (Feedback is not IMotionDiagnostics) return Task.CompletedTask;
        if (_monitoring is not null) return _firstMonitorRead.Task;
        _monitoring = Task.Run(() => MonitorAsync(lifetime, refreshed, reportError));
        return _firstMonitorRead.Task;
    }

    private async Task MonitorAsync(CancellationToken lifetime, Action refreshed,
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
            refreshed();
            _firstMonitorRead.TrySetException(error);
            throw;
        }
    }

    public void RefreshMonitorFeedback(Action<MotionAxis, Exception>? reportError = null)
    {
        if (Feedback is not IMotionDiagnostics diagnostics) return;
        foreach (var (axis, status) in MonitorAxes)
        {
            var previous = status.Snapshot.ReadError?.Message;
            status.Refresh(diagnostics, axis);
            if (status.Snapshot.ReadError is { } error && error.Message != previous)
                reportError?.Invoke(axis, error);
        }
    }

    public bool XyHomed => Axes[MotionAxis.X].State is { Homed: true }
        && (!Feedback.HasY || Axes[MotionAxis.Y].State is { Homed: true });

    // Display only; motion commands read Feedback again when they execute.
    public bool IsAtZ(double z) => !Feedback.HasZ
        || Axes[MotionAxis.Z].State is { Homed: true }
        && Math.Abs(Position.Z - z) <= MotionService.PositionToleranceMillimeters;

    public MotionPosition Position
    {
        get => _position;
        private set => Set(ref _position, value, nameof(Position));
    }

    public bool IsMoving
    {
        get => _isMoving;
        private set => Set(ref _isMoving, value, nameof(IsMoving));
    }

    public double ZMinimum { get; }
    public double ZMaximum { get; }

    private void OnPositionChanged(double x, double y, double z) =>
        Position = new(x, y, z);

    private void OnMovingChanged(bool moving) => IsMoving = moving;

    public void RefreshControlFeedback() => RefreshControlFeedback(available: true);

    public void RefreshControlFeedback(bool available)
    {
        var wasHomed = XyHomed;
        try
        {
            foreach (var (axis, status) in Axes)
                status.Update(available && Feedback.IsReady ? Feedback.GetAxisState(axis) : null);
            if (available && Feedback.IsReady)
            {
                var position = Feedback.GetPosition();
                OnPositionChanged(position.X, position.Y, position.Z);
            }
        }
        catch (IOException)
        {
            foreach (var status in Axes.Values) status.Update(null);
            throw;
        }
        finally
        {
            if (wasHomed != XyHomed)
                PropertyChanged?.Invoke(this, new(nameof(XyHomed)));
        }
    }

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
