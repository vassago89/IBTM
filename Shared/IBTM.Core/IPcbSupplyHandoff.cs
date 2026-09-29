using System;

namespace IBTM.Core;

// Each unit publishes only the handoff conditions the other unit needs.
public enum PcbSupplyHandoff { Unavailable, Holding, Released }

public interface IPcbSupplyHandoff
{
    event Action? Changed;
    PcbSupplyHandoff Handoff { get; }
    // Detected PCB with confirmed grip/fixer, including away from handoff.
    bool PcbSecured { get; }
}
