using System.Collections.Generic;
using IBTM.BoltFastening;
using IBTM.Core;

namespace IBTM.UI;

public sealed class BoltRecoveryItem
{
    public required HeatSinkSlot HeatSink { get; init; }
    public required int Number { get; init; }
    public required FasteningPass Pass { get; init; }
    public bool Completed { get; set; }
}

public sealed class BoltRecoveryViewModel(
    IReadOnlyList<BoltRecoveryItem> items,
    bool heatSink1Present,
    bool heatSink2Present)
{
    public IReadOnlyList<BoltRecoveryItem> Items { get; } = items;
    public bool HeatSink1Present { get; } = heatSink1Present;
    public bool HeatSink2Present { get; } = heatSink2Present;
}
