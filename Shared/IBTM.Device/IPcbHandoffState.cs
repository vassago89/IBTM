using System;

namespace IBTM.Device;

public interface IPcbHandoffState
{
    event Action? Changed;

    IMotionFeedback Feedback { get; }

    bool PcbSecured { get; }

    bool IsAtHandoff(bool live = true);
}

public interface IPcbHandoffReceiver : IPcbHandoffState
{
    bool HandlerRaised { get; }
}

public interface IPcbHandoffSource : IPcbHandoffState
{
    bool PcbReleased { get; }
}
