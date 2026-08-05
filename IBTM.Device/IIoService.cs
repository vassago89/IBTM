using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Device;

public interface IIoService
{
    event Action<InputIo, bool>? InputChanged;
    event Action<OutputIo, bool>? OutputChanged;

    HardwareMap Hardware { get; }

    void Initialize();
    bool GetInput(InputIo input);
    bool GetOutput(OutputIo output);
    Task WaitForInputAsync(
        InputIo input,
        bool value,
        CancellationToken cancellationToken = default);
    void SetOutput(OutputIo output, bool value);

    async Task SetOutputAndWaitAsync(
        OutputIo output,
        bool value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetOutput(output, value);
        await WaitForOutputFeedbackAsync(output, value, cancellationToken);
    }

    async Task WaitForOutputFeedbackAsync(
        OutputIo output,
        bool value,
        CancellationToken cancellationToken = default)
    {
        var feedback = Hardware.OutputFeedbacks[output];
        var expected = feedback.GetExpected(value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(feedback.TimeoutMilliseconds);

        try
        {
            await WaitForInputAsync(
                expected.Input,
                expected.Value,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IoFeedbackTimeoutException(
                output,
                value,
                expected.Input,
                expected.Value,
                feedback.TimeoutMilliseconds);
        }
    }

    void TurnOffAll();
}

public sealed class IoFeedbackTimeoutException(
    OutputIo output,
    bool outputValue,
    InputIo input,
    bool inputValue,
    int timeoutMilliseconds) : TimeoutException(
        $"{output.GetDescription()} {(outputValue ? "ON" : "OFF")} → "
        + $"{input.GetDescription()}={(inputValue ? "ON" : "OFF")} "
        + $"timeout ({timeoutMilliseconds} ms)")
{ }
