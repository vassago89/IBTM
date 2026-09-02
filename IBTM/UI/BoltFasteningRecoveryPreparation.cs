using System.Collections.Generic;
using System.Linq;
using System.Windows;
using IBTM.BoltFastening;
using IBTM.Core;

namespace IBTM.UI;

public sealed class BoltFasteningRecoveryPreparation
    : StationRecoveryPreparation
{
    private readonly BoltFasteningWork _work;
    private readonly BoltFasteningProcess _process;
    private readonly Recipe _recipe;
    private readonly bool _enabled;

    public BoltFasteningRecoveryPreparation(
        MachineState state,
        BoltFasteningWork work,
        BoltFasteningProcess process,
        Recipe recipe,
        UnitSettings units)
        : base(state, work)
    {
        _work = work;
        _process = process;
        _recipe = recipe;
        _enabled = units.BoltFastening;
    }

    public override StartPreparationType Type =>
        StartPreparationType.BoltFasteningRecovery;

    public override bool Required =>
        _enabled
        && !State.AutomaticRunning
        && _work.CarrierPresent
        && (_work.HeatSinkPresent(HeatSinkSlot.HeatSink1)
            || _work.HeatSinkPresent(HeatSinkSlot.HeatSink2));

    protected override bool Show(Window owner)
    {
        var items = new List<BoltFasteningRecoveryItem>();
        foreach (var bolt in _recipe.BoltFastening.BoltPoints
                     .Where(bolt => _work.HeatSinkPresent(bolt.HeatSink)))
        {
            if (bolt.Head == FasteningHead.Shooting)
            {
                items.Add(CreateItem(bolt, FasteningPass.Pcb));
                continue;
            }

            items.Add(CreateItem(bolt, FasteningPass.IpmSeating));
            items.Add(CreateItem(bolt, FasteningPass.IpmFinal));
        }

        var orderedItems = items
            .OrderBy(item => item.Pass)
            .ThenBy(item => item.HeatSink)
            .ThenBy(item => item.Number)
            .ToArray();
        var window = new BoltFasteningRecoveryWindow(
            new BoltFasteningRecoveryViewModel(
                orderedItems,
                _work.HeatSinkPresent(HeatSinkSlot.HeatSink1),
                _work.HeatSinkPresent(HeatSinkSlot.HeatSink2)))
        {
            Owner = owner,
        };
        if (window.ShowDialog() != true)
        {
            return false;
        }

        _process.PrepareRecovery(orderedItems
            .Where(item => item.Completed)
            .Select(item => (
                item.HeatSink,
                item.Number,
                item.Pass)));
        return true;
    }

    private BoltFasteningRecoveryItem CreateItem(
        BoltPoint bolt,
        FasteningPass pass) => new()
        {
            HeatSink = bolt.HeatSink,
            Number = bolt.Number,
            Pass = pass,
            Completed = IsCompleted(bolt, pass),
        };

    private bool IsCompleted(
        BoltPoint bolt,
        FasteningPass pass)
    {
        var assembly = _work.Assemblies.FirstOrDefault(
            item => item.HeatSink == bolt.HeatSink);
        var results = pass switch
        {
            FasteningPass.Pcb => assembly?.PcbBoltResults,
            FasteningPass.IpmSeating => assembly?.IpmSeatingResults,
            FasteningPass.IpmFinal => assembly?.IpmFinalResults,
            _ => null,
        };
        return results?.ContainsKey(bolt.Number) == true;
    }

}
