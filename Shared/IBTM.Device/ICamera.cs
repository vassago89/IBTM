using System;
using IBTM.Core;

namespace IBTM.Device;

public interface ICamera
{
    event Action<ImageFrame>? FrameReady;
    // Raised after acquisition stops; the next operation must wait for these handlers to finish.
    event Action<Exception>? LiveViewFailed;
    bool IsLiveView { get; }
    (int Width, int Height) FrameSize { get; }

    // Connect/recover the device and leave acquisition stopped.
    void Initialize();
    // During live view, capture a fresh frame without stopping or changing live exposure/gain.
    ImageFrame Capture(double exposureMicroseconds, double gain);
    void StartLiveView(double exposureMicroseconds, double gain);
    void StopLiveView();
}
