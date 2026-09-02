using System;
using System.Linq;
using System.Windows;
using IBTM.Core;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public sealed class PcbPlacementRecoveryPreparation
    : StationRecoveryPreparation
{
    private readonly PcbPlacementWork _work;
    private readonly bool _enabled;

    public PcbPlacementRecoveryPreparation(
        MachineState state,
        PcbPlacementWork work,
        UnitSettings units)
        : base(state, work)
    {
        _work = work;
        _enabled = units.PcbPlacement;
    }

    public override StartPreparationType Type =>
        StartPreparationType.PcbPlacementRecovery;

    public override bool Required =>
        _enabled
        && !State.AutomaticRunning
        && _work.CarrierPresent
        && (_work.HeatSinkPresent(HeatSinkSlot.HeatSink1)
            || _work.HeatSinkPresent(HeatSinkSlot.HeatSink2));

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

        _work.PrepareRecovery(items
            .Where(item => item.Completed)
            .Select(item => item.HeatSink));
        return true;
    }
}
