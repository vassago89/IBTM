using System;

namespace IBTM.Device;

public interface ICameraStreamService
{
    event Action<ImageFrame>? FrameReady;

    int ImageWidth { get; }
    int ImageHeight { get; }
    void Initialize();
    ImageFrame Capture();
    void StartLiveView();
    void StopLiveView();
}
