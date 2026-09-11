using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM;

internal static class DevelopmentProfile
{
    public const string Argument = "--virtual-development";

    public static bool IsEnabled
    {
        get
        {
#if VIRTUAL_DEVELOPMENT
            return true;
#else
            return false;
#endif
        }
    }

    public static void UseVirtualHardware(MachineSettings settings)
    {
        settings.Drivers.Control = ControlDriver.Virtual;
        settings.Drivers.Camera = CameraDriver.Virtual;
        settings.Drivers.Bolt = BoltDriver.Virtual;
        settings.Drivers.Light = LightDriver.Virtual;
    }

    public static async Task PrepareAsync(RecipeStore store, MachineStore database)
    {
        if (database.HasData)
        {
            return;
        }

        var settings = CreateSettings();
        var recipe = CreateRecipe();
        settings.RecipeSelection.LastRecipeName = recipe.Name;
        await store.SaveRecipeAsync(recipe);
        await settings.SaveAsync(database);
    }

    // Same synthetic teaching positions as the full-equipment WPF verification.
    private static MachineSettings CreateSettings()
    {
        var settings = new MachineSettings();
        UseVirtualHardware(settings);
        settings.Drivers.Inspection = InspectionAlgorithm.Virtual;
        foreach (var section in settings.MotionSections)
        {
            section.Settings.HorizontalHome.SearchSpeed = 200;
            section.Settings.ZHome.SearchSpeed = 100;
        }

        settings.PcbSupply.CarrierY = 10;
        settings.PcbSupply.RotationZ = 0;
        settings.PcbSupply.BufferHandoffPosition = new() { X = 80, Y = 30, Z = 10 };
        settings.PcbSupply.BufferClearZ = 20;
        settings.PcbSupply.Motion.HorizontalSpeed = 100;
        settings.PcbSupply.Motion.ZSpeed = 30;
        settings.PcbBuffer.SupplyBoundary1 = 60;
        settings.PcbBuffer.SupplyBoundary2 = 100;
        settings.PcbBuffer.PlacementBoundary1 = new() { X = 60, Y = 20 };
        settings.PcbBuffer.PlacementBoundary2 = new() { X = 100, Y = 40 };
        settings.PcbPlacementHandler.BufferEntryZ = 0;
        settings.PcbPlacementHandler.BufferHandoffPosition = new() { X = 80, Y = 30, Z = 10 };
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 100;
        settings.PcbPlacementHandler.Motion.ZSpeed = 30;
        settings.BoltFastening.SafeZ = 0;
        settings.BoltFastening.PickupPosition = new() { X = 20, Y = -60, Z = 10 };
        settings.BoltFastening.Motion.HorizontalSpeed = 60;
        settings.BoltFastening.Motion.ZSpeed = 30;
        settings.BoltFastening.ShootingHead = new()
        {
            UpperLeftLocatingPin = new() { X = 2, Y = 2 },
            LowerRightLocatingPin = new() { X = 38, Y = 28 },
        };
        settings.BoltFastening.PickupHead = new()
        {
            UpperLeftLocatingPin = new() { X = 10, Y = 2 },
            LowerRightLocatingPin = new() { X = 46, Y = 28 },
        };
        settings.InspectionGantry.Motion.HorizontalSpeed = 25;
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 2, Y = 2 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 38, Y = 28 };
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 13.48275862, Y = 15 };
        settings.NgCarrierTransfer.PickupSafeX = 13.48275862;
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 26.05172414, Y = 55.625 };
        settings.NgCarrierTransfer.Speed = 25;
        return settings;
    }

    private static Recipe CreateRecipe()
    {
        var recipe = new Recipe { Name = "Virtual Development" };
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 10, Z = 10 };
        recipe.PcbSupply.Pcb2PickPosition = new() { X = 20, Z = 10 };
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 100, Z = 10 };
        recipe.BoltFastening.PcbPreset = 4;
        recipe.BoltFastening.IpmSeatingPreset = 3;
        recipe.BoltFastening.IpmFinalPreset = 5;
        recipe.Pcb.BoltPoints = [
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, Head = FasteningHead.Shooting, X = 7, Y = 7 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink1, Head = FasteningHead.Pickup, X = 7, Y = 19 },
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 25, Y = 7 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup, X = 25, Y = 19 },
        ];
        return recipe;
    }
}
