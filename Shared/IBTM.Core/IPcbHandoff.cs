using System;

namespace IBTM.Core;

// Each unit publishes only the handoff conditions the other unit needs.
public enum PcbSupplyHandoff { Unavailable, Holding, Released }
public enum PcbPlacementHandoff { Unavailable, Holding, Clear, Returning }

public interface IPcbSupplyHandoff
{
    event Action? Changed;
    PcbSupplyHandoff Handoff { get; }
    // Live presence, including away from handoff; receipt still requires Holding.
    bool PcbDetected { get; }
}

public interface IPcbPlacementHandoff
{
    event Action? Changed;
    PcbPlacementHandoff Handoff { get; }
    // The original heat sink selected for the active return, not physical presence.
    HeatSinkSlot? ReturningPcb { get; }
}
