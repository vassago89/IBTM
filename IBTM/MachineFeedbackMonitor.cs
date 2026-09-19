using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using Microsoft.Extensions.Logging;

namespace IBTM;

internal readonly record struct MotionReadiness(bool Homed, bool ServosOn, bool Faulted);

internal sealed record MotionFeedbackSample(
    long StartedAt,
    bool Enabled,
    bool IoReady,
    MotionReadiness Readiness,
    Exception? ReadError);

// Owns device acquisition independently of views and display calculation.
public sealed class MachineFeedbackMonitor : IAsyncDisposable
{
    private static readonly TimeSpan InputPollInterval;
    private static readonly TimeSpan OutputPollInterval;
    private static readonly TimeSpan MotionPollInterval;
    private readonly UnitSettings _units;
    private readonly IIoService _io;
    private readonly ILogger<MachineFeedbackMonitor>? _log;
    private readonly CancellationTokenSource _lifetime;
    private readonly AsyncAutoResetEvent _outputsRequested;
    private readonly ConcurrentDictionary<MotionGroup, MotionFeedbackSample> _samples;
    private volatile Exception? _outputReadError;
    private volatile Exception? _failure;
    private Task? _completion;
    private Task _firstSamples = Task.CompletedTask;

    static MachineFeedbackMonitor()
    {
        InputPollInterval = TimeSpan.FromMilliseconds(10);
        OutputPollInterval = TimeSpan.FromMilliseconds(250);
        MotionPollInterval = TimeSpan.FromMilliseconds(250);
    }

    public MachineFeedbackMonitor(
        UnitSettings units,
        IIoService io,
        IoSignals ioSignals,
        PcbSupplyHandler supply,
        PcbPlacementHandler placement,
        BoltFasteningGantry fastening,
        InspectionGantry inspection,
        ILogger<MachineFeedbackMonitor>? log = null)
    {
        _lifetime = new();
        _outputsRequested = new();
        _samples = new();

        _units = units;
        _io = io;
        Io = ioSignals;
        _log = log;
        Motions = new Dictionary<MotionGroup, MotionStatus>
        {
            [MotionGroup.PcbSupply] = supply.Motion,
            [MotionGroup.PcbPlacementHandler] = placement.Motion,
            [MotionGroup.BoltFastening] = fastening.Motion,
            [MotionGroup.InspectionGantry] = inspection.Motion,
        };
        io.Faulted += OnIoFaulted;
        io.OutputChanged += OnOutputChanged;
    }

    internal event Action<MotionGroup, MotionFeedbackSample>? Sampled;
    internal event Action<Exception>? IoFaulted;
    internal event Action? Changed;

    internal IoSignals Io { get; }

    internal IReadOnlyDictionary<MotionGroup, MotionStatus> Motions { get; }

    internal Exception? Failure => _failure;

    internal Task Completion => _completion ?? Task.CompletedTask;

    internal Exception? ReadError
    {
        get
        {
            switch (true)
            {
                case true when _failure is { } failure:
                    return failure;
                case true when !_io.IsReady:
                    return null;
                case true when _outputReadError is { } outputError:
                    return outputError;
            }
            foreach (var (group, sample) in _samples)
            {
                if (_units.IsMotionEnabled(group) && sample.ReadError is { } error)
                    return error;
            }

            return null;
        }
    }

    internal MotionReadiness Readiness
    {
        get
        {
            var homed = true;
            var servosOn = true;
            var faulted = false;
            foreach (var group in Motions.Keys)
            {
                if (!_units.IsMotionEnabled(group))
                    continue;
                var sample = _samples.GetValueOrDefault(group);
                homed &= sample is { Enabled: true, IoReady: true, Readiness.Homed: true };
                servosOn &= sample is { Enabled: true, IoReady: true, Readiness.ServosOn: true };
                faulted |= sample is not { Enabled: true, IoReady: true, ReadError: null, Readiness.Faulted: false };
            }

            return new(homed, servosOn, faulted);
        }
    }

    internal Task StartAsync()
    {
        switch (true)
        {
            case true when _failure is { } failure:
                return Task.FromException(failure);
            case true when _completion is not null:
                return _firstSamples;
        }

        var firstInputs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOutputs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSamples = new List<Task> { firstInputs.Task, firstOutputs.Task };
        var monitors = new List<Task>
        {
            Task.Run(() => MonitorAsync(
                "Input", firstInputs, InputPollInterval, null, ReadInputs, FailInputs)),
            Task.Run(() => MonitorAsync(
                "Output", firstOutputs, OutputPollInterval, _outputsRequested, ReadOutputs, FailOutputs)),
        };
        foreach (var (group, motion) in Motions)
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            firstSamples.Add(first.Task);
            monitors.Add(Task.Run(() => MonitorMotionAsync(group, motion, first)));
        }

        _completion = Task.WhenAll(monitors);
        _firstSamples = Task.WhenAll(firstSamples);
        return _firstSamples;
    }

    // Shared task lifetime only; device reads and failure handling stay in named methods.
    private async Task MonitorAsync(
        string name,
        TaskCompletionSource first,
        TimeSpan interval,
        AsyncAutoResetEvent? requested,
        Action read,
        Action<Exception> failed)
    {
        var cancellationToken = _lifetime.Token;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                read();
                first.TrySetResult();
                if (requested is null)
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                else
                    await requested.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            first.TrySetCanceled(cancellationToken);
        }
        catch (Exception error)
        {
            _failure = error;
            first.TrySetException(error);
            _log?.LogError(error, "{Message}", $"{name} monitor stopped by an unexpected error. Restart the application.");
            failed(error);
            Changed?.Invoke();
            throw;
        }
    }

    private void ReadInputs()
    {
        try
        {
            // The driver publishes only complete scans and invalidates before reporting a fault.
            _io.RefreshInputs();
        }
        catch (IOException)
        {
            // The driver waits for explicit initialization; no automatic reconnect.
        }

        Io.RefreshInputs();
    }

    private void FailInputs(Exception error)
    {
        if (_io.IsReady)
            IoFaulted?.Invoke(error);
    }

    private void ReadOutputs()
    {
        try
        {
            Io.RefreshOutputs();
            _outputReadError = null;
        }
        catch (IOException error)
        {
            // Preserve a simultaneous input disconnection's original cause.
            if (_io.IsReady)
            {
                var previous = _outputReadError;
                _outputReadError = error;
                if (previous is null)
                {
                    _log?.LogError(error, "Output monitor: feedback read failed.");
                    IoFaulted?.Invoke(error);
                }
            }
        }

        if (!_io.IsReady)
            Io.InvalidateOutputs();
        Changed?.Invoke();
    }

    private void FailOutputs(Exception error)
    {
        Io.InvalidateOutputs();
        _outputReadError = error;
        IoFaulted?.Invoke(error);
    }

    private async Task MonitorMotionAsync(MotionGroup group, MotionStatus motion, TaskCompletionSource first)
    {
        var requested = new AsyncAutoResetEvent();
        motion.Feedback.StateChanged += requested.Set;
        try
        {
            await MonitorAsync(
                $"Motion {group}",
                first,
                MotionPollInterval,
                requested,
                () => ReadMotion(group, motion),
                error => FailMotion(group, motion, error)).ConfigureAwait(false);
        }
        finally
        {
            motion.Feedback.StateChanged -= requested.Set;
        }
    }

    private void ReadMotion(MotionGroup group, MotionStatus motion)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var enabled = _units.IsMotionEnabled(group);
        var ioReady = _io.IsReady;
        MotionFeedbackSample sample;
        try
        {
            // Raw diagnostics remain available for disabled axes as well.
            motion.RefreshMonitorFeedback((axis, error) => _log?.LogError(error, "{Message}", $"Motion monitor {group}/{axis}: feedback read failed."));
            ioReady = _io.IsReady;
            motion.RefreshControlFeedback(ioReady && enabled);
            var error = enabled && ioReady
                ? motion.MonitorAxes.Values.Select(axis => axis.Snapshot.ReadError)
                    .FirstOrDefault(value => value is not null)
                : null;
            var axes = motion.Axes.Values;
            var readiness = new MotionReadiness(
                axes.All(axis => axis.State is { Homed: true }),
                axes.All(axis => axis.State is { ServoOn: true }),
                axes.Any(axis => axis.State is null or { Alarm: true } or { Emergency: true }));
            sample = new(startedAt, enabled, ioReady, readiness, error);
        }
        catch (IOException error)
        {
            motion.RefreshControlFeedback(available: false);
            sample = new(startedAt, enabled, ioReady, new(false, false, true), error);
        }

        if (!_io.IsReady)
        {
            motion.RefreshControlFeedback(available: false);
            sample = sample with { IoReady = false, Readiness = new(false, false, true) };
        }

        _samples[group] = sample;
        Sampled?.Invoke(group, sample);
        Changed?.Invoke();
    }

    private void FailMotion(MotionGroup group, MotionStatus motion, Exception error)
    {
        motion.InvalidateFeedback(error);
        var sample = new MotionFeedbackSample(
            Stopwatch.GetTimestamp(), _units.IsMotionEnabled(group), _io.IsReady,
            new(false, false, true), error);
        _samples[group] = sample;
        Sampled?.Invoke(group, sample);
    }

    internal MotionReadiness ReadLiveReadiness()
    {
        var homed = true;
        var servosOn = true;
        var faulted = false;
        foreach (var (group, motion) in Motions)
        {
            if (!_units.IsMotionEnabled(group))
                continue;

            if (!motion.Feedback.IsReady)
            {
                homed = servosOn = false;
                faulted = true;
                continue;
            }

            foreach (var axis in motion.Feedback.Axes)
            {
                var state = motion.Feedback.GetAxisState(axis);
                homed &= state.Homed;
                servosOn &= state.ServoOn;
                faulted |= state.Alarm || state.Emergency;
            }
        }

        return new(homed, servosOn, faulted);
    }

    internal Task StopAsync()
    {
        _io.Faulted -= OnIoFaulted;
        _io.OutputChanged -= OnOutputChanged;
        _lifetime.Cancel();
        return Completion;
    }

    private void OnIoFaulted(Exception error)
    {
        foreach (var (group, motion) in Motions)
        {
            // Invalidate control immediately; independent raw diagnostics keep running.
            motion.RefreshControlFeedback(available: false);
            _samples[group] = new(
                Stopwatch.GetTimestamp(),
                _units.IsMotionEnabled(group),
                false,
                new(false, false, true),
                error);
        }

        IoFaulted?.Invoke(error);
        Changed?.Invoke();
        _outputsRequested.Set();
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        // A command requests a read; its requested value is not physical feedback.
        _outputsRequested.Set();
    }

    public async ValueTask DisposeAsync()
    {
        using (_lifetime)
            await StopAsync().ConfigureAwait(false);
    }
}
