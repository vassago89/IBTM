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
    private static readonly TimeSpan s_inputPollInterval;
    private static readonly TimeSpan s_outputPollInterval;
    private static readonly TimeSpan s_motionPollInterval;
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
        s_inputPollInterval = TimeSpan.FromMilliseconds(10);
        s_outputPollInterval = TimeSpan.FromMilliseconds(250);
        s_motionPollInterval = TimeSpan.FromMilliseconds(250);
    }

    public MachineFeedbackMonitor(
        UnitSettings units,
        IIoService io,
        IoSignals ioSignals,
        PcbSupplier supply,
        PcbPlacer placement,
        BoltFasteningStation fastening,
        NgCarrierTransfer inspection,
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
    internal event Action? ReadinessChanged;
    internal event Action? ReadErrorChanged;

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
                "Input", firstInputs, s_inputPollInterval, null, ReadInputs, FailInputs)),
            Task.Run(() => MonitorAsync(
                "Output", firstOutputs, s_outputPollInterval, _outputsRequested, ReadOutputs, FailOutputs)),
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
            ReadErrorChanged?.Invoke();
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
        var previousError = _outputReadError;
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
        if (!ReferenceEquals(previousError, _outputReadError))
            ReadErrorChanged?.Invoke();
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
                s_motionPollInterval,
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

        var previousReadiness = Readiness;
        var previousError = ReadError;
        _samples[group] = sample;
        // Safety checks consume every sample, including its acquisition timestamp.
        Sampled?.Invoke(group, sample);
        NotifyChanges(previousReadiness, previousError);
    }

    private void FailMotion(MotionGroup group, MotionStatus motion, Exception error)
    {
        var previousReadiness = Readiness;
        var previousError = ReadError;
        motion.InvalidateFeedback(error);
        var sample = new MotionFeedbackSample(
            Stopwatch.GetTimestamp(), _units.IsMotionEnabled(group), _io.IsReady,
            new(false, false, true), error);
        _samples[group] = sample;
        Sampled?.Invoke(group, sample);
        NotifyChanges(previousReadiness, previousError);
    }

    private void NotifyChanges(MotionReadiness previousReadiness, Exception? previousError)
    {
        if (previousReadiness != Readiness)
            ReadinessChanged?.Invoke();
        if (!ReferenceEquals(previousError, ReadError))
            ReadErrorChanged?.Invoke();
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
        var previousReadiness = Readiness;
        var previousError = ReadError;
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
        NotifyChanges(previousReadiness, previousError);
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
