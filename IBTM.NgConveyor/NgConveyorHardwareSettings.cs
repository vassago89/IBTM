using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgConveyorHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area => HardwareArea.NgConveyor;

    public override IoSection? GetSection(System.Enum signal) => signal switch
    {
        InputIo.NgConveyorPosition1Occupied or InputIo.NgConveyorPosition2Occupied
            or InputIo.NgConveyorPosition3Occupied or InputIo.NgConveyorStopperUp
            or InputIo.NgConveyorStopperDown or OutputIo.NgConveyorStopperUp
            or OutputIo.NgConveyorRun or OutputIo.NgConveyorReverse
            or OutputIo.NgConveyorNormalSpeed => IoSection.NgConveyorStorage,
        InputIo.NgCarrierEjectButton or InputIo.NgCarrierEjectCompleteButton
            or OutputIo.NgCarrierEjectLamp or OutputIo.NgCarrierEjectCompleteLamp =>
            IoSection.NgConveyorOperatorEject,
        _ => null,
    };

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
