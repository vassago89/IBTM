using System;

namespace IBTM.Device;

public interface INgCarrierTransferFeedback
{
    event Action? Changed;

    bool IsRaised { get; }

    bool IsClear { get; }
}
