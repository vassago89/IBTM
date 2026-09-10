using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgConveyorHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.NgConveyor;
        }
    }

    public override IoSection? GetSection(System.Enum signal)
    {
        return signal switch
        {
            InputIo.NgConveyorPosition1Occupied
                or InputIo.NgConveyorPosition2Occupied
                or InputIo.NgConveyorAutoMode
                or InputIo.NgConveyorStopperUp
                or InputIo.NgConveyorStopperDown
                or OutputIo.NgConveyorStopperUp
                or OutputIo.NgConveyorRun
                or OutputIo.NgConveyorReverse
                or OutputIo.NgConveyorNormalSpeed

                => IoSection.NgConveyorStorage,
            InputIo.NgCarrierEjectButton
                or InputIo.NgCarrierEjectCompleteButton
                or OutputIo.NgCarrierEjectLamp
                or OutputIo.NgCarrierEjectCompleteLamp

                => IoSection.NgConveyorOperatorEject,
            _ => null,
        };
    }

    public NgConveyorHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.NgConveyorAutoMode] = 83,
            [InputIo.NgConveyorPosition1Occupied] = 84,
            [InputIo.NgConveyorPosition2Occupied] = 85,
            [InputIo.NgConveyorStopperUp] = 87,
            [InputIo.NgConveyorStopperDown] = 88,
            [InputIo.NgCarrierEjectButton] = 89,
            [InputIo.NgCarrierEjectCompleteButton] = 90,
        };
        Outputs = new()
        {
            [OutputIo.NgConveyorStopperUp] = Output(
                70,
                71,
                InputIo.NgConveyorStopperUp,
                InputIo.NgConveyorStopperDown),
            [OutputIo.NgConveyorRun] = Output(72),
            [OutputIo.NgConveyorReverse] = Output(73),
            [OutputIo.NgConveyorNormalSpeed] = Output(74),
            [OutputIo.NgCarrierEjectLamp] = Output(75),
            [OutputIo.NgCarrierEjectCompleteLamp] = Output(76),
        };
    }
}
