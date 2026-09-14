using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Device;

public interface IIoService
{
    event Action<InputIo, bool>? InputChanged;
    event Action<OutputIo, bool>? OutputChanged;
    event Action<Exception>? Faulted;

    bool IsReady { get; }

    int TimeoutMilliseconds { get; }

    void Initialize();
    void CheckReady();
    // One complete input scan. The application owns the polling lifetime.
    // An unavailable driver waits for explicit initialization; a failed scan
    // invalidates its cache and reports Faulted before throwing.
    void RefreshInputs();
    bool GetInput(InputIo input);
    bool GetOutput(OutputIo output);
    OutputFeedback? GetOutputFeedback(OutputIo output);

    Task WaitForInputAsync(InputIo input, bool value, CancellationToken cancellationToken = default)
    {
        return WaitForInputAsync(input, value, TimeoutMilliseconds, cancellationToken);
    }

    Task WaitForInputAsync(
        InputIo input,
        bool value,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        return WaitForInputsAsync(input, value, null, timeoutMilliseconds, cancellationToken);
    }

    private async Task WaitForInputsAsync(
        InputIo input,
        bool value,
        InputIo? offInput,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        bool Matches()
        {
            return GetInput(input) == value && (offInput is null || !GetInput(offInput.Value));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Matches())
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        var completion = new AsyncAutoResetEvent();

        void OnInputChanged(InputIo changedInput, bool changedValue)
        {
            if (offInput is null
                ? changedInput == input && changedValue == value
                : (changedInput == input || changedInput == offInput) && Matches())
            {
                completion.Set();
            }
        }

        InputChanged += OnInputChanged;
        try
        {
            if (Matches())
            {
                completion.Set();
            }

            // Passage inputs retain a pulse; actuator completion requires the current pair.
            do
            {
                await completion.WaitAsync(timeout.Token);
                cancellationToken.ThrowIfCancellationRequested();
            }
            while (offInput is not null && !Matches());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (offInput is { } opposite && GetInput(input) == value)
            {
                throw new IoTimeoutException(opposite, false, timeoutMilliseconds);
            }

            throw new IoTimeoutException(input, value, timeoutMilliseconds);
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
        var feedback = GetOutputFeedback(output) ?? throw new InvalidOperationException(
            $"{output.GetDescription()} has no feedback mapping.");
        var expected = value ? feedback.OnInput : feedback.OffInput;
        var opposite = value ? feedback.OffInput : feedback.OnInput;
        await WaitForInputsAsync(expected, true, opposite, TimeoutMilliseconds, cancellationToken);
    }
}

public sealed class IoTimeoutException(InputIo input, bool inputValue, int timeoutMilliseconds) : TimeoutException(
    $"{input.GetDescription()}={(inputValue ? "ON" : "OFF")} " + $"timeout ({timeoutMilliseconds} ms)")
{
}
