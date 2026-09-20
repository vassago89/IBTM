using System;

namespace IBTM.Device;

public interface INgCarrierTransferFeedback
{
    event Action? Changed;

    bool IsRaised { get; }

    bool IsTransferPending { get; }

    bool IsClear { get; }
}
