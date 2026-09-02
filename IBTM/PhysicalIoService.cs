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
    private static readonly InputIo[] Inputs = Enum.GetValues<InputIo>();
    private readonly bool[] _inputs = new bool[
        Inputs.Max(input => (int)input) + 1];
    private readonly uint[] _rtexInputs = new uint[ajin.RtexInputWordCount];
    private CancellationTokenSource? _inputMonitor;
    private Task? _inputMonitorTask;
    private volatile bool _ready;

    public event Action<InputIo, bool>? InputChanged;
    public event Action<OutputIo, bool>? OutputChanged;
    public event Action? Faulted;
    public bool IsReady => _ready;
    public int TimeoutMilliseconds => options.TimeoutMilliseconds;

    public void Initialize()
    {
        if (_ready)
        {
            return;
        }

        _inputMonitor?.Cancel();
        _inputMonitor?.Dispose();
        alphaMotion.Initialize();
        ajin.Initialize();
        foreach (var input in Inputs)
        {
            _inputs[(int)input] = ReadInput(inputMap[input]);
        }

        _ready = true;
        _inputMonitor = new CancellationTokenSource();
        _inputMonitorTask = Task.Run(
            () => MonitorInputsAsync(_inputMonitor.Token));
    }

    public void CheckReady()
    {
        if (!_ready)
        {
            Initialize();
            return;
        }

        try
        {
            foreach (var channel in inputMap.Values.Distinct())
            {
                _ = ReadInput(channel);
            }

            foreach (var output in outputMap.Values)
            {
                _ = ReadOutput(output.Number);
                if (output.OffNumber is { } offChannel)
                {
                    _ = ReadOutput(offChannel);
                }
            }

        }
        catch
        {
            _ready = false;
            throw;
        }
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
            WriteOutput(value ? offChannel : mapping.Number, false);
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
        if (_inputMonitorTask is { IsFaulted: false } monitor)
        {
            monitor.GetAwaiter().GetResult();
        }
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
                var alphaInputs = alphaMotion.ReadInputs();
                ajin.ReadRtexInputs(_rtexInputs);
                foreach (var input in Inputs)
                {
                    var value = ReadMonitoredInput(
                        inputMap[input],
                        alphaInputs);
                    if (_inputs[(int)input] == value)
                    {
                        continue;
                    }

                    _inputs[(int)input] = value;
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
        catch
        {
            _ready = false;
            Faulted?.Invoke();
            throw;
        }
    }

    private bool ReadMonitoredInput(int channel, uint alphaInputs)
    {
        if (channel < AlphaMotionChannelCount)
        {
            return ((alphaInputs >> channel) & 1) != 0;
        }

        channel -= AlphaMotionChannelCount;
        return ((_rtexInputs[channel / 32] >> (channel % 32)) & 1) != 0;
    }
}
