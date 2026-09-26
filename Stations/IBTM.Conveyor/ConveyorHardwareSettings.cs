using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed class ConveyorHardwareSettings : IoHardwareSettings, IJsonOnDeserialized
{
    public ConveyorHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.MainConveyorAvailableFromFront2] = 17,
            [InputIo.MainConveyorReadyFromRear] = 18,
            [InputIo.MainConveyorManualMode] = 53,
            [InputIo.PcbPlacementStopperDown] = 57,
            [InputIo.PcbPlacementStopperUp] = 58,
            [InputIo.PcbPlacementBackupPlateUp] = 59,
            [InputIo.PcbPlacementBackupPlateDown] = 60,
            [InputIo.BoltFasteningStopperDown] = 64,
            [InputIo.BoltFasteningStopperUp] = 65,
            [InputIo.BoltFasteningBackupPlateUp] = 66,
            [InputIo.BoltFasteningBackupPlateDown] = 67,
            [InputIo.InspectionStopperDown] = 71,
            [InputIo.InspectionStopperUp] = 72,
            [InputIo.InspectionBackupPlateUp] = 73,
            [InputIo.InspectionBackupPlateDown] = 74,
            [InputIo.MainConveyorEntryCarrierDetected] = 56,
        };
        Outputs = new()
        {
            [OutputIo.MainConveyorReadyToFront2] = CreateOutput(17),
            [OutputIo.MainConveyorAvailableToRear] = CreateOutput(18),
            [OutputIo.PcbPlacementStopperUp] = CreateOutput(
                48,
                49,
                InputIo.PcbPlacementStopperUp,
                InputIo.PcbPlacementStopperDown),
            [OutputIo.PcbPlacementBackupPlateUp] = CreateOutput(
                50,
                51,
                InputIo.PcbPlacementBackupPlateUp,
                InputIo.PcbPlacementBackupPlateDown),
            [OutputIo.BoltFasteningStopperUp] = CreateOutput(
                52,
                53,
                InputIo.BoltFasteningStopperUp,
                InputIo.BoltFasteningStopperDown),
            [OutputIo.BoltFasteningBackupPlateUp] = CreateOutput(
                54,
                55,
                InputIo.BoltFasteningBackupPlateUp,
                InputIo.BoltFasteningBackupPlateDown),
            [OutputIo.InspectionStopperUp] = CreateOutput(
                56,
                57,
                InputIo.InspectionStopperUp,
                InputIo.InspectionStopperDown),
            [OutputIo.InspectionBackupPlateUp] = CreateOutput(
                58,
                59,
                InputIo.InspectionBackupPlateUp,
                InputIo.InspectionBackupPlateDown),
            [OutputIo.MainConveyorRun] = CreateOutput(60),
            [OutputIo.MainConveyorForward] = CreateOutput(61),
            [OutputIo.MainConveyorNormalSpeed] = CreateOutput(62),
        };
    }

    public override HardwareArea Area => HardwareArea.MainConveyor;

    void IJsonOnDeserialized.OnDeserialized()
    {
        Inputs.Remove(InputIo.Unused80);
        // Add only the newly registered signal; retain all configured addresses.
        Outputs.TryAdd(OutputIo.MainConveyorNormalSpeed, CreateOutput(62));
    }

    public override IoSection? GetSection(System.Enum signal)
    {
        switch (signal)
        {
            case InputIo.MainConveyorAvailableFromFront2 or InputIo.MainConveyorReadyFromRear
                or InputIo.MainConveyorManualMode or InputIo.MainConveyorEntryCarrierDetected
                or OutputIo.MainConveyorReadyToFront2 or OutputIo.MainConveyorAvailableToRear
                or OutputIo.MainConveyorRun or OutputIo.MainConveyorForward or OutputIo.MainConveyorNormalSpeed:
                return IoSection.MainConveyorInterfaceDrive;
            case InputIo.PcbPlacementStopperUp or InputIo.PcbPlacementStopperDown
                or InputIo.PcbPlacementBackupPlateUp or InputIo.PcbPlacementBackupPlateDown
                or OutputIo.PcbPlacementStopperUp or OutputIo.PcbPlacementBackupPlateUp:
                return IoSection.MainConveyorStation1;
            case InputIo.BoltFasteningStopperUp or InputIo.BoltFasteningStopperDown
                or InputIo.BoltFasteningBackupPlateUp or InputIo.BoltFasteningBackupPlateDown
                or OutputIo.BoltFasteningStopperUp or OutputIo.BoltFasteningBackupPlateUp:
                return IoSection.MainConveyorStation2;
            case InputIo.InspectionStopperUp or InputIo.InspectionStopperDown
                or InputIo.InspectionBackupPlateUp or InputIo.InspectionBackupPlateDown
                or OutputIo.InspectionStopperUp or OutputIo.InspectionBackupPlateUp:
                return IoSection.MainConveyorStation3;
            default:
                return null;
        }
    }
}
