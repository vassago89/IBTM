using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.BoltFastening;

public sealed partial class BoltFasteningStation : AutoUnit
{
    private readonly IBoltHead _shootingHead;
    private readonly IBoltHead _pickupHead;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly BoltFasteningWork _work;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    private readonly ILogger<BoltFasteningStation>? _log;

    public BoltFasteningStation(
        IBoltHead shootingHead,
        IBoltHead pickupHead,
        IIoService io,
        IXyMotion motion,
        BoltFasteningSettings settings,
        CarrierReferenceSettings carrierReference,
        BoltFasteningWork work,
        RecipeManager recipes,
        UnitSettings units,
        ILogger<BoltFasteningStation>? log = null)
    {
        _shootingHead = shootingHead;
        _pickupHead = pickupHead;
        _io = io;
        _motion = motion;
        _settings = settings;
        _carrierReference = carrierReference;
        _work = work;
        _recipes = recipes;
        _units = units;
        _log = log;
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
        work.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback => _motion;

    public bool PickupBoltLoaded => _io.GetInput(InputIo.PickupHeadVacuumDetected);

    internal bool ShootingTubeBoltDetected => _io.GetInput(InputIo.ShootingTubeBoltDetected);

    public BoltCylinderState PickupHeadPosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.PickupHeadUp), _io.GetInput(InputIo.PickupHeadDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public BoltCylinderState ShootingHeadPosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.ShootingHeadUp), _io.GetInput(InputIo.ShootingHeadDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public BoltCylinderState PickupTablePosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.PickupTableUp), _io.GetInput(InputIo.PickupTableDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public bool IsHorizontalMoveAllowed
    {
        get
        {
            return PickupHeadPosition == BoltCylinderState.Up
                && ShootingHeadPosition == BoltCylinderState.Up;
        }
    }

    internal BoltEscapeState ShootingEscape
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.ShootingEscapeForward),
                _io.GetInput(InputIo.ShootingEscapeBackward)))
            {
                case (true, false):
                    return BoltEscapeState.Forward;
                case (false, true):
                    return BoltEscapeState.Backward;
                default:
                    return BoltEscapeState.Between;
            }
        }
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _shootingHead.CheckReadyAsync(cancellationToken);
        await _pickupHead.CheckReadyAsync(cancellationToken);
    }

    public async Task ResetHeadsAsync(CancellationToken cancellationToken = default)
    {
        List<Exception>? failures = null;
        foreach (var head in new[] { _shootingHead, _pickupHead })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await head.ResetAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Bolt controller reset failed.", failures);
        }
    }

    internal async Task FinishFasteningAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default)
    {
        _log?.LogInformation("Bolt {Head}: requesting vacuum OFF.", head);
        await SetVacuumAsync(head, false, cancellationToken);
        _log?.LogInformation("Bolt {Head}: vacuum OFF request completed; requesting head UP.", head);
        await SetHeadDownAsync(head, false, cancellationToken);
        _log?.LogInformation("Bolt {Head}: head UP confirmed.", head);
    }

    public Task SetHeadDownAsync(
        FasteningHead head,
        bool down,
        CancellationToken cancellationToken = default)
    {
        var output = head switch
        {
            FasteningHead.Pickup => OutputIo.PickupHeadDown,
            FasteningHead.Shooting => OutputIo.ShootingHeadDown,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        return _io.SetOutputAndWaitAsync(output, down, cancellationToken);
    }

    internal Task SetPickupTableDownAsync(bool down, CancellationToken cancellationToken)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, down, cancellationToken);
    }

    public Task RaiseCylindersAsync(CancellationToken cancellationToken = default)
    {
        return Task.WhenAll(
            SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken),
            SetHeadDownAsync(FasteningHead.Shooting, false, cancellationToken));
    }

    internal Task SetShootingEscapeForwardAsync(bool forward, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, forward, cancellationToken);
    }

    internal async Task ShootBoltAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ShootingEscape != BoltEscapeState.Backward)
            await SetShootingEscapeForwardAsync(false, cancellationToken);
        await WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
        await WaitForShootingTubeClearAsync(cancellationToken);
        using var passage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? boltPassed = null;
        Exception? failure = null;
        try
        {
            await SetShootingEscapeForwardAsync(true, cancellationToken);
            _io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
            boltPassed = _io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected, true, _settings.ShootingDetectionTimeoutMilliseconds, passage.Token);
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.ShootBolt, true);
            await boltPassed;
            var arrivalStartedAt = Stopwatch.GetTimestamp();
            await WaitForShootingTubeClearAsync(cancellationToken);
            await SetShootingEscapeForwardAsync(false, cancellationToken);
            var arrivalRemaining = TimeSpan.FromSeconds(_settings.ShootingArrivalDelaySeconds)
                - Stopwatch.GetElapsedTime(arrivalStartedAt);
            if (arrivalRemaining > TimeSpan.Zero)
                await Task.Delay(arrivalRemaining, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                StopShooting(failure);
            }
            finally
            {
                passage.Cancel();
                if (boltPassed is not null)
                    await boltPassed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal Task WaitForShootingTubeClearAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected, false, _settings.ShootingDetectionTimeoutMilliseconds, cancellationToken);
    }

    internal async Task WaitForBoltSupplyAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        var boltDetected = head switch
        {
            FasteningHead.Pickup => InputIo.PickupFeederBoltDetected,
            FasteningHead.Shooting => InputIo.ShootingFeederBoltDetected,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        var changed = new AsyncAutoResetEvent();

        void OnSupplyInputChanged(InputIo input, bool value)
        {
            if (input == boltDetected)
                changed.Set();
        }

        // Subscribe before checking feedback so newly prepared supply is not missed.
        _io.InputChanged += OnSupplyInputChanged;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!_io.GetInput(boltDetected))
            {
                await changed.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _io.InputChanged -= OnSupplyInputChanged;
        }
    }

    public void StopShooting(Exception? operationFailure = null)
    {
        Exception? cleanupFailure = null;
        try
        {
            _io.SetOutput(OutputIo.ShootBolt, false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        try
        {
            _io.SetOutput(OutputIo.ShootingEscapeForward, false);
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null ? exception : new AggregateException(cleanupFailure, exception);
        }
        if (cleanupFailure is not null)
            throw operationFailure is null ? cleanupFailure : new AggregateException(operationFailure, cleanupFailure);
    }

    public void StopIoStart(FasteningHead head)
    {
        if (GetHead(head) is IoBoltHead ioHead)
        {
            ioHead.Stop();
            return;
        }

        _io.SetOutput(
            head == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart,
            false);
    }

    internal IBoltHead GetHead(FasteningHead head)
    {
        switch (head)
        {
            case FasteningHead.Shooting:
                return _shootingHead;
            case FasteningHead.Pickup:
                return _pickupHead;
            default:
                throw new ArgumentOutOfRangeException(nameof(head));
        }
    }

    public async Task SetVacuumAsync(
        FasteningHead head,
        bool on,
        CancellationToken cancellationToken,
        bool waitForFeedback = true)
    {
        var output = head == FasteningHead.Pickup
            ? OutputIo.PickupHeadVacuumPump
            : OutputIo.ShootingHeadVacuumPump;
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(output, on);
        if (waitForFeedback && head == FasteningHead.Pickup)
            await _io.WaitForInputAsync(InputIo.PickupHeadVacuumDetected, on, cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.PickupHeadVacuumDetected
            or InputIo.ShootingTubeBoltDetected
            or InputIo.PickupHeadUp
            or InputIo.PickupHeadDown
            or InputIo.ShootingHeadUp
            or InputIo.ShootingHeadDown
            or InputIo.PickupTableUp
            or InputIo.PickupTableDown
            or InputIo.ShootingEscapeForward
            or InputIo.ShootingEscapeBackward)
        {
            Changed?.Invoke();
        }
    }

}
