using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Ajin;

public sealed class AjinIoService(
    AjinController controller,
    HardwareMap hardware) : IIoService
{
    private static readonly TimeSpan InputPollInterval = TimeSpan.FromMilliseconds(10);

    public HardwareMap Hardware => hardware;

    public void Initialize() => controller.Initialize();

    public bool GetInput(InputIo input)
    {
        var channel = hardware.Inputs[input];
        var value = 0U;
        AjinController.Check(
            AjinNative.AxdiReadInportBit(
                GetModule(channel, controller.Settings.InputModuleOffset),
                channel % 32,
                ref value),
            nameof(AjinNative.AxdiReadInportBit));
        return value != 0;
    }

    public bool GetOutput(OutputIo output)
    {
        var channel = hardware.Outputs[output];
        var value = 0U;
        AjinController.Check(
            AjinNative.AxdoReadOutportBit(
                GetModule(channel, controller.Settings.OutputModuleOffset),
                channel % 32,
                ref value),
            nameof(AjinNative.AxdoReadOutportBit));
        return value != 0;
    }

    public async Task WaitForInputAsync(
        InputIo input,
        bool value,
        CancellationToken cancellationToken = default)
    {
        while (GetInput(input) != value)
        {
            await Task.Delay(InputPollInterval, cancellationToken);
        }
    }

    public void SetOutput(OutputIo output, bool value)
    {
        var channel = hardware.Outputs[output];
        AjinController.Check(
            AjinNative.AxdoWriteOutportBit(
                GetModule(channel, controller.Settings.OutputModuleOffset),
                channel % 32,
                value ? 1U : 0U),
            nameof(AjinNative.AxdoWriteOutportBit));
    }

    public void TurnOffAll()
    {
        for (var channel = 0; channel < controller.Settings.IoChannelCount; channel++)
        {
            AjinController.Check(
                AjinNative.AxdoWriteOutportBit(
                    GetModule(channel, controller.Settings.OutputModuleOffset),
                    channel % 32,
                    0),
                nameof(AjinNative.AxdoWriteOutportBit));
        }
    }

    private static int GetModule(int channel, int moduleOffset) =>
        moduleOffset + (channel / 32);
}
