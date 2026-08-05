using System;
using IBTM.Core;

namespace IBTM.Device;

public interface ICamera
{
    event Action<ImageFrame>? FrameReady;

    int ImageWidth { get; }
    int ImageHeight { get; }
    void Initialize();
    ImageFrame Capture();
    void StartLiveView();
    void StopLiveView();
}
