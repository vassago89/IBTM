using System;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualCamera(
    CameraRole role,
    Func<(double X, double Y, double Z)>? getPosition = null) : ICamera
{
    public event Action<ImageFrame>? FrameReady;

    public void Initialize()
    {
    }

    public ImageFrame Capture()
        => role == CameraRole.Inspection
            ? VirtualImageFactory.CreateInspection(getPosition!())
            : VirtualImageFactory.Fiducial;

    public void StartLiveView() => FrameReady?.Invoke(Capture());

    public void StopLiveView()
    {
    }
}
