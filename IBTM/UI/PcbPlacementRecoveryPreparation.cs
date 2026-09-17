using System;
using System.Linq;
using System.Windows;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public sealed class PcbPlacementRecoveryPreparation(
    MachineState state,
    PcbPlacementWork work,
    PcbPlacer placer,
    IIoService io,
    RecipeEditor recipeEditor) : StartPreparation(state, work, io, recipeEditor)
{
    protected override bool Show(Window owner, int version)
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
        var window = new PcbPlacementRecoveryWindow(new PcbPlacementRecoveryViewModel(items, State.RepeatEnabled))
        {
            Owner = owner,
        };
        if (window.ShowDialog() != true || !CanApply(version))
        {
            return false;
        }

        try
        {
            placer.PrepareRecovery(items.Select(item => (item.HeatSink, item.Completed)));
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(owner, exception.Message, "PCB Placement Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }
}
