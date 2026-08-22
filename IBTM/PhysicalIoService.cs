using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.Device;

namespace IBTM;

public sealed class PhysicalIoService(
    AlphaMotionController alphaMotion,
    AjinController ajin,
    IReadOnlyDictionary<InputIo, int> inputMap,
    IReadOnlyDictionary<OutputIo, OutputHardware> outputMap,
    MachineOptions options)
    : IIoService, IDisposable
{
    private const int AlphaMotionChannelCount = 16;
    private static readonly TimeSpan InputPollInterval =
        TimeSpan.FromMilliseconds(10);
    private readonly Dictionary<InputIo, bool> _inputs =
        Enum.GetValues<InputIo>().ToDictionary(input => input, _ => false);
    private CancellationTokenSource? _inputMonitor;
    private Task? _inputMonitorTask;

    public event Action<InputIo, bool>? InputChanged;
    public event Action<OutputIo, bool>? OutputChanged;
    public int TimeoutMilliseconds => options.TimeoutMilliseconds;

    public void Initialize()
    {
        alphaMotion.Initialize();
        ajin.Initialize();
        foreach (var input in Enum.GetValues<InputIo>())
        {
            _inputs[input] = GetInput(input);
        }

        _inputMonitor = new CancellationTokenSource();
        _inputMonitorTask = Task.Run(
            () => MonitorInputsAsync(_inputMonitor.Token));
    }

    public bool GetInput(InputIo input) => ReadInput(inputMap[input]);

    public bool GetOutput(OutputIo output) =>
        ReadOutput(outputMap[output].Number);

    public OutputFeedback? GetOutputFeedback(OutputIo output) =>
        outputMap[output].Feedback;

    public void SetOutput(OutputIo output, bool value)
    {
        var mapping = outputMap[output];
        if (mapping.OffNumber is { } offChannel)
        {
            WriteOutput(mapping.Number, false);
            WriteOutput(offChannel, false);
            WriteOutput(value ? mapping.Number : offChannel, true);
        }
        else
        {
            WriteOutput(mapping.Number, value);
        }

        OutputChanged?.Invoke(output, value);
    }

    public void Dispose()
    {
        _inputMonitor?.Cancel();
        _inputMonitorTask?.GetAwaiter().GetResult();
        _inputMonitor?.Dispose();
    }

    private bool ReadInput(int channel) =>
        channel < AlphaMotionChannelCount
            ? alphaMotion.ReadInput(channel)
            : ajin.ReadRtexInput(channel - AlphaMotionChannelCount);

    private bool ReadOutput(int channel) =>
        channel < AlphaMotionChannelCount
            ? alphaMotion.ReadOutput(channel)
            : ajin.ReadRtexOutput(channel - AlphaMotionChannelCount);

    private void WriteOutput(int channel, bool value)
    {
        if (channel < AlphaMotionChannelCount)
        {
            alphaMotion.WriteOutput(channel, value);
            return;
        }

        ajin.WriteRtexOutput(channel - AlphaMotionChannelCount, value);
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

                await Task.Delay(InputPollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
    }
}
