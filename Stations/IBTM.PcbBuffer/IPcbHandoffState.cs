using System;

namespace IBTM.PcbBuffer;

public interface IPcbHandoffState
{
    event Action? Changed;

    bool PcbSecured { get; }
}

public interface IPcbHandoffReceiver : IPcbHandoffState
{
    bool HandlerRaised { get; }
}

public interface IPcbHandoffSource : IPcbHandoffState
{
    bool PcbReleased { get; }
}
