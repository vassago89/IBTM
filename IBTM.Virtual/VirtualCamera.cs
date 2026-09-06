using System;
using System.Collections.Generic;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualCamera(
    Func<(double X, double Y, double Z)> getPosition,
    Func<IEnumerable<AxisPosition>> getBoltPositions) : ICamera
{
    public event Action<ImageFrame>? FrameReady;
    public event Action<Exception>? LiveViewFailed;
    public ImageFrame? SourceImage { get; set; }

    public void Initialize()
    {
    }

    public ImageFrame Capture() =>
        SourceImage ?? VirtualImageFactory.CreateInspection(
            getPosition(),
            getBoltPositions());

    public void StartLiveView()
    {
        try
        {
            FrameReady?.Invoke(Capture());
        }
        catch (Exception exception)
        {
            LiveViewFailed?.Invoke(exception);
        }
    }

    public void StopLiveView()
    {
    }
}
