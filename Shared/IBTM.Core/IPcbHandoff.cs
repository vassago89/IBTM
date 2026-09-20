using System;

namespace IBTM.Core;

// Each unit publishes only the handoff conditions the other unit needs.
public enum PcbSupplyHandoff { Unavailable, Holding, Released }
public enum PcbPlacementHandoff { Unavailable, Holding, Clear }

public interface IPcbSupplyHandoff
{
    event Action? Changed;
    PcbSupplyHandoff Handoff { get; }
}

public interface IPcbPlacementHandoff
{
    event Action? Changed;
    PcbPlacementHandoff Handoff { get; }
}
