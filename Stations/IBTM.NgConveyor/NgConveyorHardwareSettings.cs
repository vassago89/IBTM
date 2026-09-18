using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgConveyorHardwareSettings : IoHardwareSettings
{
    public NgConveyorHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.NgConveyorManualMode] = 83,
            [InputIo.NgConveyorPosition1Occupied] = 84,
            [InputIo.NgConveyorPosition2Occupied] = 85,
            [InputIo.NgConveyorStopperDown] = 87,
            [InputIo.NgConveyorStopperUp] = 88,
            [InputIo.NgCarrierEjectButton] = 89,
            [InputIo.NgCarrierEjectCompleteButton] = 90,
        };
        Outputs = new()
        {
            [OutputIo.NgConveyorStopperUp] = CreateOutput(
                70,
                71,
                InputIo.NgConveyorStopperUp,
                InputIo.NgConveyorStopperDown),
            [OutputIo.NgConveyorRun] = CreateOutput(72),
            [OutputIo.NgConveyorReverse] = CreateOutput(73),
            [OutputIo.NgCarrierEjectLamp] = CreateOutput(75),
            [OutputIo.NgCarrierEjectCompleteLamp] = CreateOutput(76),
        };
    }

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
                or InputIo.NgConveyorManualMode
                or InputIo.NgConveyorStopperUp
                or InputIo.NgConveyorStopperDown
                or OutputIo.NgConveyorStopperUp
                or OutputIo.NgConveyorRun
                or OutputIo.NgConveyorReverse
                => IoSection.NgConveyorStorage,
            InputIo.NgCarrierEjectButton
                or InputIo.NgCarrierEjectCompleteButton
                or OutputIo.NgCarrierEjectLamp
                or OutputIo.NgCarrierEjectCompleteLamp
                => IoSection.NgConveyorOperatorEject,
            _ => null,
        };
    }
}
