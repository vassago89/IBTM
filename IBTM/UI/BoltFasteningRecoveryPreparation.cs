using System.Collections.Generic;
using System.Linq;
using System.Windows;
using IBTM.BoltFastening;
using IBTM.Core;

namespace IBTM.UI;

public sealed class BoltFasteningRecoveryPreparation : StartPreparation
{
    private readonly BoltFasteningWork _work;
    private readonly BoltFasteningStation _fastening;
    private readonly Recipe _recipe;

    public BoltFasteningRecoveryPreparation(
        MachineState state,
        BoltFasteningWork work,
        BoltFasteningStation fastening,
        Recipe recipe) : base(state, work)
    {
        _work = work;
        _fastening = fastening;
        _recipe = recipe;
    }

    protected override bool Show(Window owner)
    {
        var items = new List<BoltFasteningRecoveryItem>();
        foreach (var bolt in _recipe.Pcb.GetBolts().Where(bolt => _work.HeatSinkPresent(bolt.HeatSink)))
        {
            if (bolt.Head == FasteningHead.Shooting)
            {
                items.Add(CreateItem(bolt, FasteningPass.Pcb));
                continue;
            }

            items.Add(CreateItem(bolt, FasteningPass.IpmSeating));
            items.Add(CreateItem(bolt, FasteningPass.IpmFinal));
        }

        var orderedItems = items.OrderBy(item => item.Pass)
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

        _fastening.PrepareRecovery(
            orderedItems.Select(item => (item.HeatSink, item.Number, item.Pass, item.Completed)));
        return true;
    }

    private BoltFasteningRecoveryItem CreateItem(BoltTarget bolt, FasteningPass pass)
    {
        var assembly = _work.Assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
        var results = pass switch
        {
            FasteningPass.Pcb => assembly?.PcbBoltResults,
            FasteningPass.IpmSeating => assembly?.IpmSeatingResults,
            FasteningPass.IpmFinal => assembly?.IpmFinalResults,
            _ => null,
        };
        return new()
        {
            HeatSink = bolt.HeatSink,
            Number = bolt.Number,
            Pass = pass,
            Completed = results?.ContainsKey(bolt.Number) == true,
        };
    }

}
