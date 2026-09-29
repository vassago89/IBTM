using System;

namespace IBTM.Core;
public enum PcbPlacementHandoff { Unavailable, Holding, Clear, Returning }

public interface IPcbPlacementHandoff
{
    event Action? Changed;
    PcbPlacementHandoff Handoff { get; }
    // The original heat sink selected for the active return, not physical presence.
    HeatSinkSlot? ReturningPcb { get; }
}
