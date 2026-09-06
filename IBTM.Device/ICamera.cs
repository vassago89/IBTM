using System;
using IBTM.Core;

namespace IBTM.Device;

public interface ICamera
{
    event Action<ImageFrame>? FrameReady;
    event Action<Exception>? LiveViewFailed;

    void Initialize();
    ImageFrame Capture();
    void StartLiveView();
    void StopLiveView();
}
