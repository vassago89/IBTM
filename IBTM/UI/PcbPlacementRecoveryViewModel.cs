using System.Collections.Generic;
using IBTM.Core;

namespace IBTM.UI;

public sealed class PcbPlacementRecoveryItem
{
    public required HeatSinkSlot HeatSink { get; init; }
    public bool Completed { get; set; }
}

public sealed class PcbPlacementRecoveryViewModel(IReadOnlyList<PcbPlacementRecoveryItem> items)
{
    public IReadOnlyList<PcbPlacementRecoveryItem> Items { get; } = items;
}
