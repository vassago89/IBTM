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
            [InputIo.MainConveyorEntryCarrierDetected] = 56,
        };
        Outputs = new()
        {
            [OutputIo.MainConveyorReadyToFront2] = CreateOutput(17),
            [OutputIo.MainConveyorAvailableToRear] = CreateOutput(18),
            [OutputIo.MainConveyorRun] = CreateOutput(60),
            [OutputIo.MainConveyorForward] = CreateOutput(61),
            [OutputIo.MainConveyorNormalSpeed] = CreateOutput(62),
        };
    }

    [JsonIgnore]
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
            default:
                return null;
        }
    }
}
