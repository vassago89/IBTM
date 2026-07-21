using System;

namespace IBTM.Device;

public interface ICameraStreamService
{
    event Action<ImageFrame>? FrameReady;

    int ImageWidth { get; }
    int ImageHeight { get; }
    void StartLiveView();
    void StopLiveView();
}
