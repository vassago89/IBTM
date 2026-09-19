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

    public override HardwareArea Area => HardwareArea.NgConveyor;

    public override IoSection? GetSection(System.Enum signal)
    {
        switch (signal)
        {
            case InputIo.NgConveyorPosition1Occupied:
            case InputIo.NgConveyorPosition2Occupied:
            case InputIo.NgConveyorManualMode:
            case InputIo.NgConveyorStopperUp:
            case InputIo.NgConveyorStopperDown:
            case OutputIo.NgConveyorStopperUp:
            case OutputIo.NgConveyorRun:
            case OutputIo.NgConveyorReverse:
                return IoSection.NgConveyorStorage;
            case InputIo.NgCarrierEjectButton:
            case InputIo.NgCarrierEjectCompleteButton:
            case OutputIo.NgCarrierEjectLamp:
            case OutputIo.NgCarrierEjectCompleteLamp:
                return IoSection.NgConveyorOperatorEject;
            default:
                return null;
        }
    }
}
