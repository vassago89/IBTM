using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgConveyorHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area => HardwareArea.NgConveyor;

    public NgConveyorHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.NgConveyorPosition1Occupied] = 83,
            [InputIo.NgConveyorPosition2Occupied] = 84,
            [InputIo.NgConveyorPosition3Occupied] = 85,
            [InputIo.NgConveyorStopperUp] = 86,
            [InputIo.NgConveyorStopperDown] = 87,
            [InputIo.NgCarrierEjectButton] = 88,
            [InputIo.NgCarrierEjectCompleteButton] = 89,
        };
        Outputs = new()
        {
            [OutputIo.NgConveyorStopperUp] = Output(
                69,
                70,
                InputIo.NgConveyorStopperUp,
                InputIo.NgConveyorStopperDown),
            [OutputIo.NgConveyorRun] = Output(71),
            [OutputIo.NgConveyorReverse] = Output(72),
            [OutputIo.NgConveyorNormalSpeed] = Output(73),
            [OutputIo.NgCarrierEjectLamp] = Output(74),
            [OutputIo.NgCarrierEjectCompleteLamp] = Output(75),
        };
    }
}
