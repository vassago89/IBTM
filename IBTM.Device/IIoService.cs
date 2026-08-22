using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Device;

public interface IIoService
{
    event Action<InputIo, bool>? InputChanged;
    event Action<OutputIo, bool>? OutputChanged;

    int TimeoutMilliseconds { get; }

    void Initialize();
    bool GetInput(InputIo input);
    bool GetOutput(OutputIo output);
    OutputFeedback? GetOutputFeedback(OutputIo output);

    async Task WaitForInputAsync(
        InputIo input,
        bool value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetInput(input) == value)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeoutMilliseconds);
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
        using var registration = timeout.Token.Register(
            () => completion.TrySetCanceled(timeout.Token));
        try
        {
            if (GetInput(input) == value)
            {
                completion.TrySetResult();
            }

            try
            {
                await completion.Task;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new IoTimeoutException(
                    input,
                    value,
                    TimeoutMilliseconds);
            }
        }
        finally
        {
            InputChanged -= OnInputChanged;
        }
    }
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
        var feedback = GetOutputFeedback(output)
            ?? throw new InvalidOperationException(
                $"{output.GetDescription()} has no feedback mapping.");
        var expected = value ? feedback.OnInput : feedback.OffInput;
        await WaitForInputAsync(expected, true, cancellationToken);
    }
}

public sealed class IoTimeoutException(
    InputIo input,
    bool inputValue,
    int timeoutMilliseconds) : TimeoutException(
        $"{input.GetDescription()}={(inputValue ? "ON" : "OFF")} "
        + $"timeout ({timeoutMilliseconds} ms)")
{ }
