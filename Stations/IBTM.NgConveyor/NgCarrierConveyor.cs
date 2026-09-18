using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgCarrierConveyor : AutoUnit
{
    private readonly IIoService _io;
    private readonly NgConveyorSettings _settings;
    private readonly NgShuttleFeedback _shuttle;
    private volatile Movement _movement;
    private volatile EjectionPhase _ejectionPhase;
    private bool _repeat;
    private volatile bool _requiresManualClear;

    public bool RequiresManualClear
    {
        get
        {
            return _requiresManualClear;
        }
    }

    public void ConfirmManualClear()
    {
        if (!_requiresManualClear)
            return;
        _io.CheckReady();
        if (RunCommandOn)
            throw new InvalidOperationException("Stop the NG conveyor before acknowledging its position.");
        _requiresManualClear = false;
        Changed?.Invoke();
    }

    public NgCarrierConveyor(IIoService io, NgConveyorSettings settings, NgShuttleFeedback shuttle)
    {
        _io = io;
        _settings = settings;
        _shuttle = shuttle;
        shuttle.Changed += OnShuttleChanged;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
    }

    public override event Action? Changed;

    public bool RunCommandOn
    {
        get
        {
            return _io.GetOutput(OutputIo.NgConveyorRun);
        }
    }

    public bool Position1Occupied
    {
        get
        {
            return _io.GetInput(InputIo.NgConveyorPosition1Occupied);
        }
    }

    public bool Position2Occupied
    {
        get
        {
            return _io.GetInput(InputIo.NgConveyorPosition2Occupied);
        }
    }

    public bool Position3Occupied
    {
        get
        {
            return _shuttle.CarrierDetected;
        }
    }

    public int CarrierCount
    {
        get
        {
            return (Position1Occupied ? 1 : 0) + (Position2Occupied ? 1 : 0) + (Position3Occupied ? 1 : 0);
        }
    }

    public int AlarmCarrierCount
    {
        get
        {
            return _settings.AlarmCarrierCount;
        }
    }

    public bool AlarmRequired
    {
        get
        {
            return CarrierCount >= AlarmCarrierCount;
        }
    }

    public bool Full
    {
        get
        {
            return CarrierCount == 3;
        }
    }

    private bool EjectRequested
    {
        get
        {
            return _io.GetInput(InputIo.NgCarrierEjectButton);
        }
    }

    private bool EjectConfirmed
    {
        get
        {
            return _io.GetInput(InputIo.NgCarrierEjectCompleteButton);
        }
    }

    internal bool CanAcceptCarrier(bool? runCommandOn = null)
    {
        return !_requiresManualClear
            && _movement == Movement.None
            && _ejectionPhase == EjectionPhase.Idle
            && !Full
            && !NeedsCompaction
            && (_repeat || !EjectRequested)
            && !(runCommandOn ?? RunCommandOn);
    }

    internal bool ShuttleCanRaise
    {
        get
        {
            return !RunCommandOn
                && ((!Position3Occupied
                        && (_movement == Movement.ToPosition1 && Position1Occupied
                            || _movement == Movement.ToPosition2 && Position2Occupied))
                    || State == NgConveyorState.Full);
        }
    }

    public NgConveyorState State
    {
        get
        {
            return ReadState(RunCommandOn);
        }
    }

    public NgConveyorState ReadState(bool runCommandOn)
    {
        if (_requiresManualClear)
            return NgConveyorState.ManualClearRequired;
        // A stopped transfer with no presence feedback has no known physical location.
        // The saved destination is work history, not permission to guess and resume.
        if (!runCommandOn
            && (_movement switch
            {
                Movement.ToPosition1 => !Position1Occupied && !Position3Occupied,
                Movement.ToPosition2 => !Position2Occupied && !Position3Occupied,
                Movement.Compacting => !Position1Occupied && !Position2Occupied,
                _ => false,
            }))
        {
            return NgConveyorState.CarrierPositionUnknown;
        }

        switch (_ejectionPhase)
        {
            case EjectionPhase.Ejecting:
                return NgConveyorState.EjectingCarrier;
            case EjectionPhase.WaitingForConfirmation:
                if (!_io.GetInput(InputIo.NgConveyorStopperUp)
                    || _io.GetInput(InputIo.NgConveyorStopperDown))
                {
                    return NgConveyorState.SecuringEjectStopper;
                }

                if (NeedsCompaction)
                {
                    return NgConveyorState.CompactingCarriers;
                }

                return EjectConfirmed
                    ? NgConveyorState.AcknowledgingEject
                    : NgConveyorState.WaitingForEjectConfirmation;
            case EjectionPhase.WaitingForButtonRelease:
                return NgConveyorState.WaitingForEjectButtonRelease;
        }

        if (Position1Occupied
            && !_repeat
            && EjectRequested
            && _shuttle.Lift == NgShuttleLiftState.Up)
        {
            return NgConveyorState.EjectingCarrier;
        }

        switch (_movement)
        {
            case Movement.ToPosition1:
                return Position1Occupied && !Position3Occupied
                    ? NgConveyorState.WaitingForShuttleUp
                    : NgConveyorState.MovingToPosition1;
            case Movement.ToPosition2:
                return Position2Occupied && !Position3Occupied
                    ? NgConveyorState.WaitingForShuttleUp
                    : NgConveyorState.MovingToPosition2;
            case Movement.Compacting:
                return NgConveyorState.CompactingCarriers;
        }

        if (NeedsCompaction)
        {
            return NgConveyorState.CompactingCarriers;
        }

        if (Position3Occupied)
        {
            if (Full)
            {
                return NgConveyorState.Full;
            }

            if (_shuttle.Lift != NgShuttleLiftState.Down)
                return NgConveyorState.WaitingForShuttleDown;
            if (!Position1Occupied)
                return NgConveyorState.MovingToPosition1;
            return !Position2Occupied ? NgConveyorState.MovingToPosition2 : NgConveyorState.Full;
        }

        return Position1Occupied ? NgConveyorState.ReadyToEject : NgConveyorState.WaitingForCarrier;
    }

    public Task RunMotorAsync(CancellationToken cancellationToken)
    {
        return RunControlledAsync(
            async token =>
            {
                StartConveyor(token);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        if (_requiresManualClear)
            throw new InvalidOperationException("NG conveyor movement was interrupted. Check the stopped carrier position, then press RESET.");
        _repeat = repeat;
        return RunControlledAsync(
            async token =>
            {
                if (!repeat && _ejectionPhase == EjectionPhase.Idle && EjectRequested)
                {
                    _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
                    Changed?.Invoke();
                }

                await RunLoopAsync(ExecuteAsync, token);
            },
            cancellationToken);
    }

    private async Task RunControlledAsync(
        Func<CancellationToken, Task> run,
        CancellationToken cancellationToken)
    {
        Exception? cancellationFailure = null;
        void StopOnCancellation()
        {
            try
            {
                StopConveyor();
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }
        }

        Exception? failure = null;
        try
        {
            using (cancellationToken.Register(StopOnCancellation))
            {
                try
                {
                    await run(cancellationToken);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            if (cancellationFailure is not null)
                failure = failure is null ? cancellationFailure : new AggregateException(failure, cancellationFailure);
            if (failure is not null)
                ExceptionDispatchInfo.Throw(failure);
        }
        finally
        {
            _repeat = false;
            if (_movement != Movement.None || _ejectionPhase != EjectionPhase.Idle)
                _requiresManualClear = true;
            _movement = Movement.None;
            _ejectionPhase = EjectionPhase.Idle;
            try
            {
                Stop();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    public async Task ReturnToShuttleAsync(CancellationToken cancellationToken)
    {
        if (_requiresManualClear)
            throw new InvalidOperationException("NG conveyor movement was interrupted. Check the stopped carrier position, then press RESET.");
        if (CarrierCount != 1)
            throw new InvalidOperationException("NG return requires one carrier with known presence feedback.");
        if (_shuttle.Lift != NgShuttleLiftState.Down && !Position3Occupied)
            throw new InvalidOperationException("Lower the NG shuttle before returning the carrier.");

        _movement = Movement.None;
        _ejectionPhase = EjectionPhase.Idle;
        await SetStopperDownAsync(true, cancellationToken);
        await RunUntilAsync(InputIo.NgShuttleCarrierDetected, true, true, cancellationToken);
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        UpdateOperatorOutputs();
        var state = State;
        TraceStep(state);
        switch (state)
        {
            case NgConveyorState.MovingToPosition1:
                return MoveCarrierAsync(Movement.ToPosition1, cancellationToken);
            case NgConveyorState.MovingToPosition2:
                return MoveCarrierAsync(Movement.ToPosition2, cancellationToken);
            case NgConveyorState.WaitingForShuttleUp:
                if (_shuttle.Lift != NgShuttleLiftState.Up)
                {
                    return WaitForChangeAsync(cancellationToken);
                }

                _movement = Movement.None;
                Changed?.Invoke();
                break;
            case NgConveyorState.EjectingCarrier:
                return EjectCarrierAsync(cancellationToken);
            case NgConveyorState.SecuringEjectStopper:
                return SetStopperDownAsync(false, cancellationToken);
            case NgConveyorState.CompactingCarriers:
                return CompactCarriersAsync(cancellationToken);
            case NgConveyorState.AcknowledgingEject:
                _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
                _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, false);
                Changed?.Invoke();
                break;
            default:
                return WaitForChangeAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        OutputIo[] outputs = [
            OutputIo.NgConveyorRun,
            OutputIo.NgCarrierEjectLamp,
            OutputIo.NgCarrierEjectCompleteLamp,
        ];
        List<Exception>? failures = null;
        foreach (var output in outputs)
        {
            try
            {
                _io.SetOutput(output, false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures?.Count == 1)
            ExceptionDispatchInfo.Throw(failures[0]);
        if (failures is not null)
            throw new AggregateException("NG conveyor outputs could not all be stopped.", failures);
    }

    private bool NeedsCompaction
    {
        get
        {
            return _movement == Movement.Compacting || !Position1Occupied && Position2Occupied;
        }
    }

    private async Task MoveCarrierAsync(
        Movement movement,
        CancellationToken cancellationToken)
    {
        var destination = movement switch
        {
            Movement.ToPosition1 => InputIo.NgConveyorPosition1Occupied,
            Movement.ToPosition2 => InputIo.NgConveyorPosition2Occupied,
            _ => throw new ArgumentOutOfRangeException(nameof(movement)),
        };
        _movement = movement;
        await SetStopperDownAsync(false, cancellationToken);
        await RunUntilAsync(destination, true, false, cancellationToken);

        Changed?.Invoke();
    }

    private async Task EjectCarrierAsync(CancellationToken cancellationToken)
    {
        _ejectionPhase = EjectionPhase.Ejecting;
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, false);
        if (Position1Occupied)
        {
            await SetStopperDownAsync(true, cancellationToken);
            await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, false, false, cancellationToken);
        }

        await SetStopperDownAsync(false, cancellationToken);
        _ejectionPhase = EjectionPhase.WaitingForConfirmation;
        Changed?.Invoke();
        _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, true);
    }

    private async Task CompactCarriersAsync(CancellationToken cancellationToken)
    {
        _movement = Movement.Compacting;
        await SetStopperDownAsync(false, cancellationToken);
        await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, true, false, cancellationToken);
        _movement = Movement.None;
        Changed?.Invoke();
    }

    private void UpdateOperatorOutputs()
    {
        var alarm = AlarmRequired && _ejectionPhase == EjectionPhase.Idle;
        _io.SetOutput(
            OutputIo.NgCarrierEjectCompleteLamp,
            _ejectionPhase == EjectionPhase.WaitingForConfirmation);
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, alarm);
    }

    private Task SetStopperDownAsync(bool down, CancellationToken cancellationToken)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, !down, cancellationToken);
    }

    internal async Task RunUntilAsync(
        InputIo destination,
        bool occupied,
        bool reverse,
        CancellationToken cancellationToken)
    {
        if (_io.GetInput(destination) == occupied)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            StartConveyor(cancellationToken, reverse);
            await _io.WaitForInputAsync(destination, occupied, cancellationToken);
        }
        catch (Exception exception)
        {
            _requiresManualClear = true;
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                StopConveyor();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    private void StartConveyor(CancellationToken cancellationToken, bool reverse = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorReverse, reverse);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorRun, true);
    }

    private void StopConveyor()
    {
        _io.SetOutput(OutputIo.NgConveyorRun, false);
    }

    private void NotifyChanged()
    {
        if (_ejectionPhase == EjectionPhase.WaitingForButtonRelease
            && !EjectRequested
            && !EjectConfirmed)
        {
            _ejectionPhase = EjectionPhase.Idle;
        }

        Changed?.Invoke();
    }

    private void OnShuttleChanged()
    {
        if (_shuttle.Lift == NgShuttleLiftState.Up
            && ShuttleCanRaise)
        {
            _movement = Movement.None;
        }

        NotifyChanged();
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input == InputIo.NgConveyorPosition1Occupied
            && Position1Occupied
            && _movement == Movement.Compacting)
        {
            _movement = Movement.None;
        }

        if (input is InputIo.NgConveyorPosition1Occupied
            or InputIo.NgConveyorPosition2Occupied
            or InputIo.NgConveyorStopperUp
            or InputIo.NgConveyorStopperDown
            or InputIo.NgCarrierEjectButton
            or InputIo.NgCarrierEjectCompleteButton)
        {
            NotifyChanged();
        }
    }

    private void OnOutputChanged(OutputIo output, bool _)
    {
        if (output == OutputIo.NgConveyorRun)
        {
            Changed?.Invoke();
        }
    }

    private enum EjectionPhase
    {
        Idle,
        Ejecting,
        WaitingForConfirmation,
        WaitingForButtonRelease,
    }

    private enum Movement
    {
        None,
        ToPosition1,
        ToPosition2,
        Compacting,
    }
}
