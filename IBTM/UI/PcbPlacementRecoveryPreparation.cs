using System;
using System.Linq;
using System.Windows;
using IBTM.Core;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public sealed class PcbPlacementRecoveryPreparation
    : StartPreparation
{
    private readonly PcbPlacementWork _work;

    public PcbPlacementRecoveryPreparation(
        MachineState state,
        PcbPlacementWork work)
        : base(state, work)
    {
        _work = work;
    }

    public override StartPreparationType Type =>
        StartPreparationType.PcbPlacementRecovery;

    protected override bool Show(Window owner)
    {
        var items = Enum.GetValues<HeatSinkSlot>()
            .Where(_work.HeatSinkPresent)
            .Select(heatSink => new PcbPlacementRecoveryItem
            {
                HeatSink = heatSink,
                Completed = _work.Assemblies.Any(assembly =>
                    assembly.HeatSink == heatSink),
            })
            .ToArray();
        var window = new PcbPlacementRecoveryWindow(
            new PcbPlacementRecoveryViewModel(items))
        {
            Owner = owner,
        };
        if (window.ShowDialog() != true)
        {
            return false;
        }

        _work.PrepareRecovery(items.Select(item =>
            (item.HeatSink, item.Completed)));
        return true;
    }
}
