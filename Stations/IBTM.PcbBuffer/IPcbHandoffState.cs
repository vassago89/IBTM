using System;

namespace IBTM.PcbBuffer;

public interface IPcbHandoffState
{
    event Action? Changed;

    bool PcbSecured { get; }
}
