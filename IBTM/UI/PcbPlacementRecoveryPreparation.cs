using System;
using System.Linq;
using System.Windows;
using IBTM.Core;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public sealed class PcbPlacementRecoveryPreparation(
    MachineState state,
    PcbPlacementWork work) : StartPreparation(state, work)
{
    protected override bool Show(Window owner)
    {
        var items = Enum.GetValues<HeatSinkSlot>()
            .Where(work.HeatSinkPresent)
            .Select(
                heatSink =>
                    new PcbPlacementRecoveryItem
                    {
                        HeatSink = heatSink,
                        Completed = work.Assemblies.Any(assembly => assembly.HeatSink == heatSink),
                    })
            .ToArray();
        var window = new PcbPlacementRecoveryWindow(new PcbPlacementRecoveryViewModel(items))
        {
            Owner = owner,
        };
        if (window.ShowDialog() != true)
        {
            return false;
        }

        work.PrepareRecovery(items.Select(item => (item.HeatSink, item.Completed)));
        return true;
    }
}
