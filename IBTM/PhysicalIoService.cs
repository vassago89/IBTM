using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.Core;
using IBTM.Device;

namespace IBTM;

public sealed class PhysicalIoService(
    AlphaMotionController alphaMotion,
    AjinController ajin,
    IReadOnlyDictionary<InputIo, int> inputMap,
    IReadOnlyDictionary<OutputIo, OutputHardware> outputMap,
    MachineOptions options,
    ApplicationLog? log = null)
    : IIoService, IDisposable
{
    private const int AlphaMotionChannelCount = 16;
    private static readonly TimeSpan InputPollInterval =
        TimeSpan.FromMilliseconds(10);
    private static readonly InputIo[] Inputs = Enum.GetValues<InputIo>();
    private readonly bool[] _inputs = new bool[
        Inputs.Max(input => (int)input) + 1];
    private readonly bool[] _inputScan = new bool[
        Inputs.Max(input => (int)input) + 1];
    private readonly InputIo[] _changedInputs = new InputIo[Inputs.Length];
    private readonly uint[] _rtexInputs = new uint[ajin.RtexInputWordCount];
    private readonly Lock _lifecycleGate = new();
    private CancellationTokenSource? _inputMonitor;
    private Task? _inputMonitorTask;
    private volatile bool _ready;

    public event Action<InputIo, bool>? InputChanged;
    public event Action<OutputIo, bool>? OutputChanged;
    public event Action<Exception>? Faulted;
    public bool IsReady => _ready;
    public int TimeoutMilliseconds => options.TimeoutMilliseconds;

    public void Initialize()
    {
        lock (_lifecycleGate)
        {
            if (_ready)
            {
                return;
            }

            var stage = "Stopping previous input scan";
            try
            {
                StopInputMonitor();
                stage = "AlphaMotion initialization";
                log?.Write(stage + " started.");
                alphaMotion.Initialize();
                log?.Write(stage + " completed.");
                stage = "AJIN initialization / motion parameter loading";
                log?.Write(stage + " started.");
                ajin.Initialize();
                log?.Write(stage + " completed.");
                foreach (var input in Inputs)
                {
                    stage = $"Initial DI read: {input}, channel={inputMap[input]}";
                    var value = ReadInput(inputMap[input]);
                    _inputs[(int)input] = value;
                    log?.Write($"{stage}: {(value ? "ON" : "OFF")}");
                }

                _ready = true;
                _inputMonitor = new CancellationTokenSource();
                var cancellationToken = _inputMonitor.Token;
                _inputMonitorTask = Task.Run(
                    () => MonitorInputsAsync(cancellationToken));
            }
            catch (Exception exception)
            {
                log?.Error($"{stage} failed. Input scan is not running.", exception);
                throw;
            }
        }
    }

    public void CheckReady()
    {
        lock (_lifecycleGate)
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
            catch (Exception exception)
            {
                _ready = false;
                log?.Error("Control I/O readiness check failed.", exception);
                throw;
            }
        }
    }

    public bool GetInput(InputIo input) =>
        Volatile.Read(ref _inputs[(int)input]);

    public bool GetOutput(OutputIo output) =>
        ReadOutput(outputMap[output].Number);

    public OutputFeedback? GetOutputFeedback(OutputIo output) =>
        outputMap[output].Feedback;

    public void SetOutput(OutputIo output, bool value)
    {
        var mapping = outputMap[output];
        try
        {
            if (mapping.OffNumber is { } offChannel)
            {
                WriteOutput(value ? offChannel : mapping.Number, false);
                WriteOutput(value ? mapping.Number : offChannel, true);
            }
            else
            {
                WriteOutput(mapping.Number, value);
            }
        }
        catch (Exception exception)
        {
            log?.Error($"DO {output}, channel={mapping.Number}, paired OFF={mapping.OffNumber}: write {(value ? "ON" : "OFF")} failed.", exception);
            throw;
        }

        log?.Write($"DO {output}, channel={mapping.Number}: {(value ? "ON" : "OFF")}");
        OutputChanged?.Invoke(output, value);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            StopInputMonitor();
            _ready = false;
        }
    }

    private void StopInputMonitor()
    {
        _inputMonitor?.Cancel();
        // Monitor failures have already been reported through Faulted.
        _inputMonitorTask?.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)
            .GetAwaiter().GetResult();
        _inputMonitor?.Dispose();
        _inputMonitor = null;
        _inputMonitorTask = null;
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
        var stage = "Starting input scan";
        try
        {
            log?.Write("Input scan started (10 ms interval).");
            var firstScan = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stage = "AlphaMotion input read";
                var alphaInputs = alphaMotion.ReadInputs();
                stage = "AJIN input read";
                ajin.ReadRtexInputs(_rtexInputs);
                if (firstScan)
                {
                    log?.Write($"First input scan completed. AlphaMotion=0x{alphaInputs:X8}; AJIN words={string.Join(", ", _rtexInputs.Select(word => $"0x{word:X8}"))}.");
                    firstScan = false;
                }
                stage = "Input address mapping";
                foreach (var input in Inputs)
                {
                    _inputScan[(int)input] = ReadMonitoredInput(
                        inputMap[input],
                        alphaInputs);
                }

                var changedCount = 0;
                foreach (var input in Inputs)
                {
                    var index = (int)input;
                    var value = _inputScan[index];
                    if (Volatile.Read(ref _inputs[index]) == value)
                    {
                        continue;
                    }

                    Volatile.Write(ref _inputs[index], value);
                    _changedInputs[changedCount++] = input;
                }

                for (var index = 0; index < changedCount; index++)
                {
                    var input = _changedInputs[index];
                    stage = $"Input change notification: {input}, channel={inputMap[input]}";
                    log?.Write($"DI {input}, channel={inputMap[input]}: {(_inputScan[(int)input] ? "ON" : "OFF")}");
                    InputChanged?.Invoke(input, _inputScan[(int)input]);
                }

                await Task.Delay(InputPollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            log?.Write("Input scan stopped.");
        }
        catch (Exception exception)
        {
            _ready = false;
            log?.Error($"Input scan stopped by error during {stage}. Inputs will no longer update until initialization succeeds.", exception);
            Faulted?.Invoke(exception);
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
