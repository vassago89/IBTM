using System.Collections.Generic;
using System.ComponentModel;

namespace IBTM.Device;

public sealed record MotionPosition(double X, double Y, double Z);

public sealed class MotionStatus : INotifyPropertyChanged
{
    private MotionPosition _position;
    private bool _isMoving;

    public MotionStatus(IMotionFeedback motion)
    {
        var position = motion.GetPosition();
        _position = new(position.X, position.Y, position.Z);
        _isMoving = motion.IsMoving;

        var zRange = motion.GetRange(MotionAxis.Z);
        ZMinimum = zRange?.Minimum ?? 0;
        ZMaximum = zRange?.Maximum ?? 0;

        motion.PositionChanged += OnPositionChanged;
        motion.MovingChanged += OnMovingChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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
