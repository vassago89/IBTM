using System;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualCamera(
    Func<(double X, double Y, double Z)> getPosition) : ICamera
{
    public event Action<ImageFrame>? FrameReady;

    public void Initialize()
    {
    }

    public ImageFrame Capture() =>
        VirtualImageFactory.CreateInspection(getPosition());

    public void StartLiveView() => FrameReady?.Invoke(Capture());

    public void StopLiveView()
    {
    }
}
