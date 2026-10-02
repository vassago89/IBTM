using System;

namespace IBTM.Core;
public enum PcbPlacementHandoff { Unavailable, Holding, Clear }

public interface IPcbPlacementHandoff
{
    event Action? Changed;
    PcbPlacementHandoff Handoff { get; }
}
