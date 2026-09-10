using System;

namespace IBTM.PcbBuffer;

public interface IBufferPlacementState
{
    event Action? Changed;

    bool PcbSecured { get; }
}
