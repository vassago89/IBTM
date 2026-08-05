using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Ajin;

public sealed class AjinIoService(
    AjinController controller,
    HardwareMap hardware) : IIoService, IDisposable
{
    private static readonly TimeSpan InputPollInterval = TimeSpan.FromMilliseconds(10);
    private readonly Dictionary<InputIo, bool> _inputs =
        Enum.GetValues<InputIo>().ToDictionary(input => input, _ => false);
    private CancellationTokenSource? _inputMonitor;

    public event Action<InputIo, bool>? InputChanged;
    public event Action<OutputIo, bool>? OutputChanged;

    public HardwareMap Hardware => hardware;

    public void Initialize()
    {
        controller.Initialize();
        foreach (var input in Enum.GetValues<InputIo>())
        {
            _inputs[input] = GetInput(input);
        }

        _inputMonitor = new CancellationTokenSource();
        _ = MonitorInputsAsync(_inputMonitor.Token);
    }

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
        cancellationToken.ThrowIfCancellationRequested();
        if (GetInput(input) == value)
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnInputChanged(InputIo changedInput, bool changedValue)
        {
            if (changedInput == input && changedValue == value)
            {
                completion.TrySetResult();
            }
        }

        InputChanged += OnInputChanged;
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        try
        {
            if (GetInput(input) == value)
            {
                completion.TrySetResult();
            }

            await completion.Task;
        }
        finally
        {
            InputChanged -= OnInputChanged;
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
        OutputChanged?.Invoke(output, value);
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

        foreach (var output in Enum.GetValues<OutputIo>())
        {
            OutputChanged?.Invoke(output, false);
        }
    }

    public void Dispose()
    {
        _inputMonitor?.Cancel();
        _inputMonitor?.Dispose();
    }

    private async Task MonitorInputsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                foreach (var input in Enum.GetValues<InputIo>())
                {
                    var value = GetInput(input);
                    if (_inputs[input] == value)
                    {
                        continue;
                    }

                    _inputs[input] = value;
                    InputChanged?.Invoke(input, value);
                }

                await Task.Delay(InputPollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static int GetModule(int channel, int moduleOffset) =>
        moduleOffset + (channel / 32);
}
