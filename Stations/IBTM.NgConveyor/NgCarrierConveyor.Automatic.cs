using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed partial class NgCarrierConveyor
{
    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        _repeat = repeat;
        using var motor = new ConveyorRun(_io, OutputIo.NgConveyorRun, cancellationToken, OutputIo.NgCarrierEjectLamp, OutputIo.NgCarrierEjectCompleteLamp);
        try
        {
            if (_units.NgConveyor && !repeat && _ejectionPhase == EjectionPhase.Idle && EjectRequested)
            {
                _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
                Changed?.Invoke();
            }

            BeginRun();
            while (!cancellationToken.IsCancellationRequested)
            {
                var step = GetNextStep(RunCommandOn);
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
        if (Position3Occupied && IsAcceptCarrierAllowed(runCommandOn))
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
                    return NgConveyorState.SecuringEjectStopper;
                return NeedsCompaction
                    ? NgConveyorState.CompactingCarriers
                    : NgConveyorState.WaitingForEjectConfirmation;
            case EjectionPhase.WaitingForButtonRelease:
                return NgConveyorState.WaitingForEjectButtonRelease;
        }

        if (Position1Occupied
            && !_repeat
            && EjectRequested
            && ShuttleLift == NgShuttleLiftState.Up)
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
            return NgConveyorState.CompactingCarriers;
        if (!Position3Occupied)
            return Position1Occupied ? NgConveyorState.ReadyToEject : NgConveyorState.WaitingForCarrier;
        if (Full)
            return NgConveyorState.Full;
        if (ShuttleLift != NgShuttleLiftState.Down)
            return NgConveyorState.WaitingForShuttleDown;
        if (!Position1Occupied)
            return NgConveyorState.MovingToPosition1;
        return !Position2Occupied ? NgConveyorState.MovingToPosition2 : NgConveyorState.Full;
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
                await SetStopperDownAsync(false, cancellationToken);
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
                if (Position1Occupied)
                {
                    await SetStopperDownAsync(true, cancellationToken);
                    await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, false, false, cancellationToken);
                }

                await SetStopperDownAsync(false, cancellationToken);
                _ejectionPhase = EjectionPhase.WaitingForConfirmation;
                Changed?.Invoke();
                _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, true);
                break;
            case NgConveyorState.SecuringEjectStopper:
                await SetStopperDownAsync(false, cancellationToken);
                break;
            case NgConveyorState.CompactingCarriers:
                _movement = Movement.Compacting;
                await SetStopperDownAsync(false, cancellationToken);
                await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, true, false, cancellationToken);
                _movement = Movement.None;
                Changed?.Invoke();
                break;
            case NgConveyorState.WaitingForEjectConfirmation when EjectConfirmed:
                _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
                _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, false);
                Changed?.Invoke();
                break;
            default:
                return false;
        }
        return true;
    }




}
