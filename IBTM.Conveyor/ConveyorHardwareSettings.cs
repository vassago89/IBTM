using IBTM.Device;

namespace IBTM.Conveyor;

public sealed class ConveyorHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area => HardwareArea.MainConveyor;

    public ConveyorHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.MainConveyorAvailableFromFront2] = 17,
            [InputIo.MainConveyorReadyFromRear] = 18,
            [InputIo.PcbPlacementCarrierJigPresent] = 56,
            [InputIo.PcbPlacementStopperUp] = 57,
            [InputIo.PcbPlacementStopperDown] = 58,
            [InputIo.PcbPlacementBackupPlateUp] = 59,
            [InputIo.PcbPlacementBackupPlateDown] = 60,
            [InputIo.BoltFasteningCarrierJigPresent] = 63,
            [InputIo.BoltFasteningStopperUp] = 64,
            [InputIo.BoltFasteningStopperDown] = 65,
            [InputIo.BoltFasteningBackupPlateUp] = 66,
            [InputIo.BoltFasteningBackupPlateDown] = 67,
            [InputIo.InspectionCarrierJigPresent] = 70,
            [InputIo.InspectionStopperUp] = 71,
            [InputIo.InspectionStopperDown] = 72,
            [InputIo.InspectionBackupPlateUp] = 73,
            [InputIo.InspectionBackupPlateDown] = 74,
        };
        Outputs = new()
        {
            [OutputIo.MainConveyorReadyToFront2] = Output(17),
            [OutputIo.MainConveyorAvailableToRear] = Output(18),
            [OutputIo.PcbPlacementStopperUp] = Output(
                48,
                49,
                InputIo.PcbPlacementStopperUp,
                InputIo.PcbPlacementStopperDown),
            [OutputIo.PcbPlacementBackupPlateUp] = Output(
                50,
                51,
                InputIo.PcbPlacementBackupPlateUp,
                InputIo.PcbPlacementBackupPlateDown),
            [OutputIo.BoltFasteningStopperUp] = Output(
                52,
                53,
                InputIo.BoltFasteningStopperUp,
                InputIo.BoltFasteningStopperDown),
            [OutputIo.BoltFasteningBackupPlateUp] = Output(
                54,
                55,
                InputIo.BoltFasteningBackupPlateUp,
                InputIo.BoltFasteningBackupPlateDown),
            [OutputIo.InspectionStopperUp] = Output(
                56,
                57,
                InputIo.InspectionStopperUp,
                InputIo.InspectionStopperDown),
            [OutputIo.InspectionBackupPlateUp] = Output(
                58,
                59,
                InputIo.InspectionBackupPlateUp,
                InputIo.InspectionBackupPlateDown),
            [OutputIo.MainConveyorRun] = Output(60),
            [OutputIo.MainConveyorReverse] = Output(61),
            [OutputIo.MainConveyorNormalSpeed] = Output(62),
        };
    }
}
