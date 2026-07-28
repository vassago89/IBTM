using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public interface IIoService
{
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
        $"{output} {(outputValue ? "ON" : "OFF")} → "
        + $"{input}={(inputValue ? "ON" : "OFF")} "
        + $"timeout ({timeoutMilliseconds} ms)")
{
    public OutputIo Output { get; } = output;
    public bool OutputValue { get; } = outputValue;
    public InputIo Input { get; } = input;
    public bool InputValue { get; } = inputValue;
}
