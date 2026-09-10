using IBTM.Device;

namespace IBTM.Conveyor;

public sealed class ConveyorHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.MainConveyor;
        }
    }

    public override IoSection? GetSection(System.Enum signal)
    {
        return signal switch
        {
            InputIo.MainConveyorAvailableFromFront2
                or InputIo.MainConveyorReadyFromRear
                or InputIo.MainConveyorAutoMode
                or InputIo.MainConveyorEntryCarrierDetected
                or InputIo.MainConveyorExitCarrierDetected
                or OutputIo.MainConveyorReadyToFront2
                or OutputIo.MainConveyorAvailableToRear
                or OutputIo.MainConveyorRun
                or OutputIo.MainConveyorForward
                => IoSection.MainConveyorInterfaceDrive,
            InputIo.PcbPlacementCarrierPresent
                or InputIo.PcbPlacementStopperUp
                or InputIo.PcbPlacementStopperDown
                or InputIo.PcbPlacementBackupPlateUp
                or InputIo.PcbPlacementBackupPlateDown
                or OutputIo.PcbPlacementStopperDown
                or OutputIo.PcbPlacementBackupPlateDown
                => IoSection.MainConveyorStation1,
            InputIo.BoltFasteningCarrierPresent
                or InputIo.BoltFasteningStopperUp
                or InputIo.BoltFasteningStopperDown
                or InputIo.BoltFasteningBackupPlateUp
                or InputIo.BoltFasteningBackupPlateDown
                or OutputIo.BoltFasteningStopperDown
                or OutputIo.BoltFasteningBackupPlateDown
                => IoSection.MainConveyorStation2,
            InputIo.InspectionCarrierPresent
                or InputIo.InspectionStopperUp
                or InputIo.InspectionStopperDown
                or InputIo.InspectionBackupPlateUp
                or InputIo.InspectionBackupPlateDown
                or OutputIo.InspectionStopperDown
                or OutputIo.InspectionBackupPlateDown
                => IoSection.MainConveyorStation3,
            _ => null,
        };
    }

    public ConveyorHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.MainConveyorAvailableFromFront2] = 17,
            [InputIo.MainConveyorReadyFromRear] = 18,
            [InputIo.MainConveyorAutoMode] = 53,
            [InputIo.PcbPlacementCarrierPresent] = 56,
            [InputIo.PcbPlacementStopperDown] = 57,
            [InputIo.PcbPlacementStopperUp] = 58,
            [InputIo.PcbPlacementBackupPlateUp] = 59,
            [InputIo.PcbPlacementBackupPlateDown] = 60,
            [InputIo.BoltFasteningCarrierPresent] = 63,
            [InputIo.BoltFasteningStopperDown] = 64,
            [InputIo.BoltFasteningStopperUp] = 65,
            [InputIo.BoltFasteningBackupPlateUp] = 66,
            [InputIo.BoltFasteningBackupPlateDown] = 67,
            [InputIo.InspectionCarrierPresent] = 70,
            [InputIo.InspectionStopperDown] = 71,
            [InputIo.InspectionStopperUp] = 72,
            [InputIo.InspectionBackupPlateUp] = 73,
            [InputIo.InspectionBackupPlateDown] = 74,
            [InputIo.MainConveyorEntryCarrierDetected] = 91,
            [InputIo.MainConveyorExitCarrierDetected] = 92,
        };
        Outputs = new()
        {
            [OutputIo.MainConveyorReadyToFront2] = Output(17),
            [OutputIo.MainConveyorAvailableToRear] = Output(18),
            [OutputIo.PcbPlacementStopperDown] = Output(
                48,
                49,
                InputIo.PcbPlacementStopperDown,
                InputIo.PcbPlacementStopperUp),
            [OutputIo.PcbPlacementBackupPlateDown] = Output(
                50,
                51,
                InputIo.PcbPlacementBackupPlateDown,
                InputIo.PcbPlacementBackupPlateUp),
            [OutputIo.BoltFasteningStopperDown] = Output(
                52,
                53,
                InputIo.BoltFasteningStopperDown,
                InputIo.BoltFasteningStopperUp),
            [OutputIo.BoltFasteningBackupPlateDown] = Output(
                54,
                55,
                InputIo.BoltFasteningBackupPlateDown,
                InputIo.BoltFasteningBackupPlateUp),
            [OutputIo.InspectionStopperDown] = Output(
                56,
                57,
                InputIo.InspectionStopperDown,
                InputIo.InspectionStopperUp),
            [OutputIo.InspectionBackupPlateDown] = Output(
                58,
                59,
                InputIo.InspectionBackupPlateDown,
                InputIo.InspectionBackupPlateUp),
            [OutputIo.MainConveyorRun] = Output(60),
            [OutputIo.MainConveyorForward] = Output(61),
        };
    }
}
