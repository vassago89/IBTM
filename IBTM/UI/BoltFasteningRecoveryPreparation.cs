using System.Collections.Generic;
using System.Linq;
using System.Windows;
using IBTM.BoltFastening;
using IBTM.Core;

namespace IBTM.UI;

public sealed class BoltFasteningRecoveryPreparation(
    MachineState state,
    BoltFasteningWork work,
    BoltFasteningStation fastening,
    Recipe recipe) : StartPreparation(state, work)
{
    protected override bool Show(Window owner)
    {
        var items = new List<BoltFasteningRecoveryItem>();
        foreach (var bolt in recipe.Pcb.GetBolts().Where(bolt => work.HeatSinkPresent(bolt.HeatSink)))
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
                work.HeatSinkPresent(HeatSinkSlot.HeatSink1),
                work.HeatSinkPresent(HeatSinkSlot.HeatSink2)))
        {
            Owner = owner,
        };
        if (window.ShowDialog() != true)
        {
            return false;
        }

        fastening.PrepareRecovery(
            orderedItems.Select(item => (item.HeatSink, item.Number, item.Pass, item.Completed)));
        return true;
    }

    private BoltFasteningRecoveryItem CreateItem(BoltTarget bolt, FasteningPass pass)
    {
        var assembly = work.Assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
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
