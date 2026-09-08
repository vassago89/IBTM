using System;
using IBTM.Core;

namespace IBTM.Device;

public interface ICamera
{
    event Action<ImageFrame>? FrameReady;
    event Action<Exception>? LiveViewFailed;
    (int Width, int Height) FrameSize { get; }

    void Initialize();
    ImageFrame Capture(double exposureMicroseconds, double gain);
    void StartLiveView(double exposureMicroseconds, double gain);
    void StopLiveView();
}
