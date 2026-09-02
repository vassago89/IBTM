using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualCamera(
    Func<(double X, double Y, double Z)> getPosition,
    Func<IEnumerable<AxisPosition>> getBoltPositions) : ICamera
{
    public event Action<ImageFrame>? FrameReady;

    public void Initialize()
    {
    }

    public ImageFrame Capture() =>
        VirtualImageFactory.CreateInspection(
            getPosition(),
            getBoltPositions().ToArray());

    public void StartLiveView() => FrameReady?.Invoke(Capture());

    public void StopLiveView()
    {
    }
}
