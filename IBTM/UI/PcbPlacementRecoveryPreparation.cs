using System;
using System.Linq;
using System.Windows;
using IBTM.Core;
using IBTM.Device;
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
        UnitSettings units,
        IIoService io)
        : base(
            state,
            io,
            InputIo.PcbPlacementCarrierPresent,
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementHeatSink2Present)
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
        var window = new Station1RecoveryWindow(
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
