using System;
using System.Collections.Generic;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed record VirtualDataMatrix(AxisPosition Center, double Width, double Height, string Text);

public sealed class VirtualCamera(
    Func<(double X, double Y, double Z)> getPosition,
    Func<IEnumerable<AxisPosition>> getBoltPositions,
    Func<IEnumerable<VirtualDataMatrix>>? getDataMatrices = null) : ICamera
{
    public event Action<ImageFrame>? FrameReady;
    public event Action<Exception>? LiveViewFailed;
    public bool IsLiveView { get; private set; }
    public ImageFrame? SourceImage { get; set; }

    public (int Width, int Height) FrameSize
    {
        get
        {
            return SourceImage is { } image
                ? (image.Width, image.Height)
                : (VirtualImageFactory.Width, VirtualImageFactory.Height);
        }
    }

    public bool BoltsPresent { get; set; } = true;

    public void Initialize()
    {
        StopLiveView();
    }

    public ImageFrame Capture(double exposureMicroseconds, double gain)
    {
        return SourceImage ?? VirtualImageFactory.CreateInspection(
            getPosition(),
            BoltsPresent ? getBoltPositions() : [],
            getDataMatrices?.Invoke() ?? []);
    }

    public void StartLiveView(double exposureMicroseconds, double gain)
    {
        IsLiveView = true;
        try
        {
            FrameReady?.Invoke(Capture(exposureMicroseconds, gain));
        }
        catch (Exception exception)
        {
            IsLiveView = false;
            LiveViewFailed?.Invoke(exception);
        }
    }

    public void StopLiveView()
    {
        IsLiveView = false;
    }
}
