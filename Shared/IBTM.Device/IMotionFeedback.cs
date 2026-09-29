using System;
using System.Collections.Generic;

namespace IBTM.Device;

public interface IMotionFeedback
{
    event Action<double, double, double>? PositionChanged;
    event Action<bool>? MovingChanged;
    event Action? StateChanged;

    IReadOnlyList<MotionAxis> Axes { get; }

    bool IsReady { get; }

    bool HasY { get; }

    bool HasZ { get; }

    bool IsMoving { get; }

    bool IsMovingHorizontal { get; }

    MotionCommand Command { get; }

    (double X, double Y, double Z) Position { get; }
    AxisState GetAxisState(MotionAxis axis);
}
