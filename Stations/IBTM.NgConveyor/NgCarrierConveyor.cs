using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed partial class NgCarrierConveyor : AutoUnit
{
    private readonly IIoService _io;
    private readonly NgConveyorSettings _settings;
    private INgCarrierTransferFeedback? _transfer;
    private readonly UnitSettings _units;
    private volatile Movement _movement;
    private volatile EjectionPhase _ejectionPhase;
    private bool _repeat;

    public NgCarrierConveyor(
        IIoService io,
        NgConveyorSettings settings,
        UnitSettings units)
    {
        _io = io;
        _settings = settings;
        _units = units;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
    }

    public void AttachTransfer(INgCarrierTransferFeedback transfer)
    {
        if (_transfer is not null)
            throw new InvalidOperationException("NG transfer feedback is already attached.");
        _transfer = transfer;
        // Wake this loop without echoing Changed back to the inspection station.
        transfer.Changed += OnChanged;
    }

    public override event Action? Changed;

    public NgShuttleLiftState ShuttleLift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgShuttleUp), _io.GetInput(InputIo.NgShuttleDown)))
            {
                case (true, false):
                    return NgShuttleLiftState.Up;
                case (false, true):
                    return NgShuttleLiftState.Down;
                default:
                    return NgShuttleLiftState.Between;
            }
        }
    }

    public int CarrierCount => (_io.GetInput(InputIo.NgConveyorPosition1Occupied) ? 1 : 0)
        + (_io.GetInput(InputIo.NgConveyorPosition2Occupied) ? 1 : 0)
        + (_io.GetInput(InputIo.NgShuttleCarrierDetected) ? 1 : 0);

    public bool AlarmRequired => CarrierCount >= _settings.AlarmCarrierCount;

    public bool Full => CarrierCount == 3;

    private bool IsShuttleRaiseRequired(bool runCommandOn)
    {
        return !runCommandOn
            && ((!_io.GetInput(InputIo.NgShuttleCarrierDetected)
                    && (_movement == Movement.None
                        || _movement == Movement.ToPosition1 && _io.GetInput(InputIo.NgConveyorPosition1Occupied)
                        || _movement == Movement.ToPosition2 && _io.GetInput(InputIo.NgConveyorPosition2Occupied)))
                || Full && _movement == Movement.None);
    }

    // Pending ownership is cleared by the release operation, never by presence DI.
    private bool IsTransferClear => _transfer?.IsClear == true
        && _io.GetInput(InputIo.NgCarrierGripperOpen)
        && !_io.GetInput(InputIo.NgCarrierGripperClosed);

    public bool IsReceiveAllowed
    {
        get
        {
            return ShuttleLift == NgShuttleLiftState.Up
                && !_io.GetInput(InputIo.NgShuttleCarrierDetected)
                && IsAcceptCarrierAllowed();
        }
    }

    private bool NeedsCompaction => _movement == Movement.Compacting
        || !_io.GetInput(InputIo.NgConveyorPosition1Occupied)
            && _io.GetInput(InputIo.NgConveyorPosition2Occupied);

    private bool IsAcceptCarrierAllowed(bool? runCommandOn = null)
    {
        return _movement == Movement.None
            && _ejectionPhase == EjectionPhase.Idle
            && !Full
            && !NeedsCompaction
            && (_repeat || !_io.GetInput(InputIo.NgCarrierEjectButton))
            && !(runCommandOn ?? _io.GetOutput(OutputIo.NgConveyorRun));
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.NgConveyorPosition1Occupied
            or InputIo.NgConveyorPosition2Occupied
            or InputIo.NgConveyorStopperUp
            or InputIo.NgConveyorStopperDown
            or InputIo.NgCarrierEjectButton
            or InputIo.NgCarrierEjectCompleteButton
            or InputIo.NgShuttleUp
            or InputIo.NgShuttleDown
            or InputIo.NgShuttleCarrierDetected)
        {
            Changed?.Invoke();
        }
    }

    private void OnOutputChanged(OutputIo output, bool value)
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

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        _repeat = repeat;
        using var motor = new ConveyorRun(_io, OutputIo.NgConveyorRun, cancellationToken, OutputIo.NgCarrierEjectLamp, OutputIo.NgCarrierEjectCompleteLamp);
        try
        {
            if (_units.NgConveyor && !repeat && _ejectionPhase == EjectionPhase.Idle && _io.GetInput(InputIo.NgCarrierEjectButton))
            {
                _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
                Changed?.Invoke();
            }

            BeginRun();
            while (!cancellationToken.IsCancellationRequested)
            {
                var step = GetNextStep(_io.GetOutput(OutputIo.NgConveyorRun));
                if (!await ExecuteStepAsync(step, cancellationToken))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
        finally
        {
            _repeat = false;
            _movement = Movement.None;
            _ejectionPhase = EjectionPhase.Idle;
            EndRun(cancellationToken);
        }
    }

    public NgConveyorState GetNextStep(bool runCommandOn)
    {
        if (!_units.NgConveyor)
            return NgConveyorState.WaitingForCarrier;
        if (ShuttleLift != NgShuttleLiftState.Up && IsShuttleRaiseRequired(runCommandOn))
            return IsTransferClear
                ? NgConveyorState.RaisingShuttle : NgConveyorState.WaitingForTransferRelease;
        if (_io.GetInput(InputIo.NgShuttleCarrierDetected) && IsAcceptCarrierAllowed(runCommandOn))
        {
            if (!IsTransferClear)
                return NgConveyorState.WaitingForTransferRelease;
            if (ShuttleLift != NgShuttleLiftState.Down)
                return NgConveyorState.LoweringShuttle;
        }
        // A stopped transfer with no presence feedback has no known physical location.
        // The saved destination is work history, not permission to guess and resume.
        if (!runCommandOn
            && (_movement switch
            {
                Movement.ToPosition1 => !_io.GetInput(InputIo.NgConveyorPosition1Occupied) && !_io.GetInput(InputIo.NgShuttleCarrierDetected),
                Movement.ToPosition2 => !_io.GetInput(InputIo.NgConveyorPosition2Occupied) && !_io.GetInput(InputIo.NgShuttleCarrierDetected),
                Movement.Compacting => !_io.GetInput(InputIo.NgConveyorPosition1Occupied) && !_io.GetInput(InputIo.NgConveyorPosition2Occupied),
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
                    return NgConveyorState.SecuringEjectStopper;
                return NeedsCompaction
                    ? NgConveyorState.CompactingCarriers
                    : NgConveyorState.WaitingForEjectConfirmation;
            case EjectionPhase.WaitingForButtonRelease:
                return NgConveyorState.WaitingForEjectButtonRelease;
        }

        if (_io.GetInput(InputIo.NgConveyorPosition1Occupied)
            && !_repeat
            && _io.GetInput(InputIo.NgCarrierEjectButton)
            && ShuttleLift == NgShuttleLiftState.Up)
        {
            return NgConveyorState.EjectingCarrier;
        }

        switch (_movement)
        {
            case Movement.ToPosition1:
                return _io.GetInput(InputIo.NgConveyorPosition1Occupied) && !_io.GetInput(InputIo.NgShuttleCarrierDetected)
                    ? NgConveyorState.WaitingForShuttleUp
                    : NgConveyorState.MovingToPosition1;
            case Movement.ToPosition2:
                return _io.GetInput(InputIo.NgConveyorPosition2Occupied) && !_io.GetInput(InputIo.NgShuttleCarrierDetected)
                    ? NgConveyorState.WaitingForShuttleUp
                    : NgConveyorState.MovingToPosition2;
            case Movement.Compacting:
                return NgConveyorState.CompactingCarriers;
        }

        if (NeedsCompaction)
            return NgConveyorState.CompactingCarriers;
        if (!_io.GetInput(InputIo.NgShuttleCarrierDetected))
            return _io.GetInput(InputIo.NgConveyorPosition1Occupied) ? NgConveyorState.ReadyToEject : NgConveyorState.WaitingForCarrier;
        if (Full)
            return NgConveyorState.Full;
        if (ShuttleLift != NgShuttleLiftState.Down)
            return NgConveyorState.WaitingForShuttleDown;
        if (!_io.GetInput(InputIo.NgConveyorPosition1Occupied))
            return NgConveyorState.MovingToPosition1;
        return !_io.GetInput(InputIo.NgConveyorPosition2Occupied) ? NgConveyorState.MovingToPosition2 : NgConveyorState.Full;
    }

    private async Task<bool> ExecuteStepAsync(NgConveyorState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var alarm = _units.NgConveyor && AlarmRequired && _ejectionPhase == EjectionPhase.Idle;
        _io.SetOutput(
            OutputIo.NgCarrierEjectCompleteLamp,
            _ejectionPhase == EjectionPhase.WaitingForConfirmation);
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, alarm);
        EnterStep(state, waitingFor: state switch
        {
            NgConveyorState.WaitingForTransferRelease =>
                $"pickup Up={_transfer?.IsRaised}, pending={_transfer?.IsTransferPending}, "
                    + $"gripper Open={_io.GetInput(InputIo.NgCarrierGripperOpen)}, "
                    + $"Closed={_io.GetInput(InputIo.NgCarrierGripperClosed)}",
            NgConveyorState.WaitingForShuttleDown =>
                $"shuttle={ShuttleLift}, accept={IsAcceptCarrierAllowed()}, movement={_movement}, ejection={_ejectionPhase}",
            _ => null,
        });
        switch (state)
        {
            case NgConveyorState.LoweringShuttle:
                await SetShuttleDownAsync(true, cancellationToken);
                break;
            case NgConveyorState.RaisingShuttle:
                await SetShuttleDownAsync(false, cancellationToken);
                break;
            case NgConveyorState.MovingToPosition1 or NgConveyorState.MovingToPosition2:
                var toPosition1 = state == NgConveyorState.MovingToPosition1;
                _movement = toPosition1 ? Movement.ToPosition1 : Movement.ToPosition2;
                await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true, cancellationToken);
                await RunUntilAsync(
                    toPosition1 ? InputIo.NgConveyorPosition1Occupied : InputIo.NgConveyorPosition2Occupied,
                    true, false, cancellationToken);
                Changed?.Invoke();
                break;
            case NgConveyorState.WaitingForShuttleUp:
                if (ShuttleLift != NgShuttleLiftState.Up)
                    return false;

                _movement = Movement.None;
                Changed?.Invoke();
                break;
            case NgConveyorState.EjectingCarrier:
                _ejectionPhase = EjectionPhase.Ejecting;
                _io.SetOutput(OutputIo.NgCarrierEjectLamp, false);
                if (_io.GetInput(InputIo.NgConveyorPosition1Occupied))
                {
                    await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, false, cancellationToken);
                    await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, false, false, cancellationToken);
                }

                await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true, cancellationToken);
                _ejectionPhase = EjectionPhase.WaitingForConfirmation;
                Changed?.Invoke();
                _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, true);
                break;
            case NgConveyorState.SecuringEjectStopper:
                await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true, cancellationToken);
                break;
            case NgConveyorState.CompactingCarriers:
                _movement = Movement.Compacting;
                await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true, cancellationToken);
                await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, true, false, cancellationToken);
                _movement = Movement.None;
                Changed?.Invoke();
                break;
            case NgConveyorState.WaitingForEjectConfirmation when _io.GetInput(InputIo.NgCarrierEjectCompleteButton):
                _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
                _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, false);
                Changed?.Invoke();
                break;
            case NgConveyorState.WaitingForEjectButtonRelease when !_io.GetInput(InputIo.NgCarrierEjectButton) && !_io.GetInput(InputIo.NgCarrierEjectCompleteButton):
                _ejectionPhase = EjectionPhase.Idle;
                Changed?.Invoke();
                break;
            default:
                return false;
        }
        return true;
    }

    public Task SetShuttleDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTransferClear)
            throw new MotionInterlockException("Complete the NG transfer release and raise the open pickup before moving the shuttle.");
        return _io.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, down, cancellationToken);
    }

    public async Task RunMotorAsync(CancellationToken cancellationToken)
    {
        using var motor = new ConveyorRun(_io, OutputIo.NgConveyorRun, cancellationToken, OutputIo.NgCarrierEjectLamp, OutputIo.NgCarrierEjectCompleteLamp);
        try
        {
            StartConveyor(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
        finally
        {
            _repeat = false;
            _movement = Movement.None;
            _ejectionPhase = EjectionPhase.Idle;
        }
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
            await Task.Delay(5000);

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
                _io.SetOutput(OutputIo.NgConveyorRun, false);
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
        _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorReverse, reverse);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorRun, true);
    }
}
