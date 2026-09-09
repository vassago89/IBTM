using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class SettingsViewModel
{
    private readonly ILightController _light;
    private readonly ApplicationLog _log;
    [ObservableProperty] private int _lightTestChannel;
    [ObservableProperty] private int _lightTestLevel = 80;
    [ObservableProperty] private bool _lightTestOn;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestLightCommand), nameof(OffTestLightCommand))]
    private int? _pendingLightOffChannel;
    [ObservableProperty] private string _lightTestMessage = "Test only: does not change recipe brightness.";
    public string ActiveLightConnection { get; }

    private bool CanTestLight() => CanEditSettings && PendingLightOffChannel is null
        && !OffTestLightCommand.IsRunning;

    private bool CanOffTestLight() => TestLightCommand.IsRunning
        || PendingLightOffChannel is not null && !_operations.IsShuttingDown && !_state.IsRunning;

    private void OnLightCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
        TestLightCommand.NotifyCanExecuteChanged();
        OffTestLightCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOffTestLight))]
    private async Task OffTestLightAsync()
    {
        if (TestLightCommand.IsRunning)
        {
            TestLightCommand.Cancel();
            if (TestLightCommand.ExecutionTask is { } test) await test;
            return;
        }
        if (!CanOffTestLight() || PendingLightOffChannel is not { } channel) return;
        try
        {
            using var operation = _operations.Link();
            var failure = await TurnTestLightOffAsync(channel);
            LightTestMessage = failure?.Message ?? $"OFF command sent · channel {channel}.";
        }
        catch (OperationCanceledException) { }
        finally { RefreshCommands(); }
    }

    private async Task<Exception?> TurnTestLightOffAsync(int channel)
    {
        PendingLightOffChannel = channel;
        try
        {
            // Reconnect if needed, but never send ON/brightness during an OFF retry.
            await Task.Run(() => { _light.Initialize(); _light.TurnOff(channel); });
            _log.Write($"Lighting test OFF command sent: channel={channel}.");
            PendingLightOffChannel = null;
            return null;
        }
        catch (Exception exception)
        {
            var failure = new InvalidOperationException(
                $"OFF failed on channel {channel}; light state is unknown. Press OFF to retry. {exception.Message}", exception);
            _log.Error("Lighting test cleanup failed; light may still be ON.", exception);
            return failure;
        }
        finally { LightTestOn = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestLight), IncludeCancelCommand = true)]
    private async Task TestLightAsync(CancellationToken cancellationToken)
    {
        if (!CanTestLight() || LightTestOn) return;
        // MOVS commands have a single channel digit; zero addresses all channels.
        if (LightTestChannel is < 1 or > 9 || LightTestLevel is < 0 or > 255)
        {
            LightTestMessage = "Use a single channel (1–9) and brightness 0–255.";
            return;
        }

        var channel = LightTestChannel;
        var level = LightTestLevel;
        var startingAlarm = _state.Alarm;
        try
        {
            using var operation = _operations.Link(cancellationToken);
            void StopWhenUnavailable()
            {
                if (!_state.ManualMode || _state.Alarm != startingAlarm || _operations.IsShuttingDown)
                    operation.Cancel();
            }
            _state.Changed += StopWhenUnavailable;
            var initialized = false;
            Exception? failure = null;
            try
            {
                StopWhenUnavailable();
                LightTestMessage = $"Connecting: {ActiveLightConnection}…";
                _log.Write($"Lighting test started: driver={ActiveLightDriver}, connection={ActiveLightConnection}, channel={channel}, level={level}.");
                await Task.Run(() =>
                {
                    operation.Token.ThrowIfCancellationRequested();
                    _light.Initialize();
                    initialized = true;
                    operation.Token.ThrowIfCancellationRequested();
                    _light.SetLevel(channel, level);
                    operation.Token.ThrowIfCancellationRequested();
                    _light.TurnOn(channel);
                }, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                PendingLightOffChannel = channel;
                LightTestOn = true;
                LightTestMessage = $"ON command sent · channel {channel}, level {level}. Press OFF to finish.";
                _log.Write($"Lighting test ON command sent: channel={channel}, level={level}.");
                // Keep the operation owned while illuminated, including OFF cleanup.
                // This blocks automatic/motion admission and lets STOP cancel the test.
                await Task.Delay(Timeout.Infinite, operation.Token);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                failure = exception;
                _log.Error("Lighting test failed.", exception);
            }
            finally
            {
                _state.Changed -= StopWhenUnavailable;
                if (initialized)
                {
                    // Required cleanup and retries target the captured channel, not edited settings.
                    var offFailure = await TurnTestLightOffAsync(channel);
                    failure = offFailure ?? failure;
                }
                LightTestOn = false;
                LightTestMessage = failure?.Message ?? (initialized
                    ? $"OFF command sent · channel {channel}." : "Lighting test cancelled.");
            }
        }
        catch (OperationCanceledException) { LightTestMessage = "Lighting test cancelled."; }
        finally { RefreshCommands(); }
    }
}
