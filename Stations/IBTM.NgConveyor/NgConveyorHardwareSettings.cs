using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgConveyorHardwareSettings : IoHardwareSettings, IJsonOnDeserialized
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
            [OutputIo.NgConveyorNormalSpeed] = CreateOutput(74),
            [OutputIo.NgCarrierEjectLamp] = CreateOutput(75),
            [OutputIo.NgCarrierEjectCompleteLamp] = CreateOutput(76),
        };
    }

    public override HardwareArea Area => HardwareArea.NgConveyor;

    void IJsonOnDeserialized.OnDeserialized()
    {
        Outputs.TryAdd(OutputIo.NgConveyorNormalSpeed, CreateOutput(74));
    }

    public override IoSection? GetSection(System.Enum signal)
    {
        switch (signal)
        {
            case InputIo.NgConveyorPosition1Occupied or InputIo.NgConveyorPosition2Occupied or InputIo.NgConveyorManualMode
                or InputIo.NgConveyorStopperUp or InputIo.NgConveyorStopperDown or OutputIo.NgConveyorStopperUp
                or OutputIo.NgConveyorRun or OutputIo.NgConveyorReverse or OutputIo.NgConveyorNormalSpeed:
                return IoSection.NgConveyorStorage;
            case InputIo.NgCarrierEjectButton or InputIo.NgCarrierEjectCompleteButton
                or OutputIo.NgCarrierEjectLamp or OutputIo.NgCarrierEjectCompleteLamp:
                return IoSection.NgConveyorOperatorEject;
            default:
                return null;
        }
    }
}
