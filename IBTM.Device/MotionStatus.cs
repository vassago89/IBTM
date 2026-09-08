using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace IBTM.Device;

public sealed record MotionPosition(double X, double Y, double Z);

public sealed class MotionStatus : INotifyPropertyChanged
{
    private MotionPosition _position;
    private bool _isMoving;

    public MotionStatus(IMotionFeedback motion)
    {
        Feedback = motion;
        Axes = motion.Axes.ToDictionary(axis => axis, _ => new AxisStatus());
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
    public IReadOnlyDictionary<MotionAxis, AxisStatus> Axes { get; }
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

    public void RefreshAxes()
    {
        var wasHomed = XyHomed;
        try
        {
            foreach (var (axis, status) in Axes)
                status.Update(Feedback.IsReady ? Feedback.GetAxisState(axis) : null);
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
