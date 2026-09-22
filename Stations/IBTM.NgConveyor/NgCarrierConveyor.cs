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
    private readonly INgCarrierTransferFeedback _transfer;
    private readonly UnitSettings _units;
    private volatile Movement _movement;
    private volatile EjectionPhase _ejectionPhase;
    private bool _repeat;

    public NgCarrierConveyor(
        IIoService io,
        NgConveyorSettings settings,
        INgCarrierTransferFeedback transfer,
        UnitSettings units)
    {
        _io = io;
        _settings = settings;
        _transfer = transfer;
        _units = units;
        transfer.Changed += NotifyChanged;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
    }

    public override event Action? Changed;

    public bool RunCommandOn => _io.GetOutput(OutputIo.NgConveyorRun);

    public bool Position1Occupied => _io.GetInput(InputIo.NgConveyorPosition1Occupied);

    public bool Position2Occupied => _io.GetInput(InputIo.NgConveyorPosition2Occupied);

    public bool Position3Occupied => _io.GetInput(InputIo.NgShuttleCarrierDetected);

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

    public int CarrierCount => (Position1Occupied ? 1 : 0) + (Position2Occupied ? 1 : 0) + (Position3Occupied ? 1 : 0);

    public int AlarmCarrierCount => _settings.AlarmCarrierCount;

    public bool AlarmRequired => CarrierCount >= AlarmCarrierCount;

    public bool Full => CarrierCount == 3;

    private bool EjectRequested => _io.GetInput(InputIo.NgCarrierEjectButton);

    private bool EjectConfirmed => _io.GetInput(InputIo.NgCarrierEjectCompleteButton);

    internal bool IsShuttleRaiseAllowed
    {
        get
        {
            return !RunCommandOn
                && ((!Position3Occupied
                        && (_movement == Movement.ToPosition1 && Position1Occupied
                            || _movement == Movement.ToPosition2 && Position2Occupied))
                    || GetConveyorState(RunCommandOn) == NgConveyorState.Full);
        }
    }

    public NgConveyorState State => GetState(RunCommandOn);

    private bool NeedsCompaction => _movement == Movement.Compacting || !Position1Occupied && Position2Occupied;

    internal bool IsAcceptCarrierAllowed(bool? runCommandOn = null)
    {
        return _movement == Movement.None
            && _ejectionPhase == EjectionPhase.Idle
            && !Full
            && !NeedsCompaction
            && (_repeat || !EjectRequested)
            && !(runCommandOn ?? RunCommandOn);
    }

    public NgConveyorState GetState(bool runCommandOn)
    {
        if (_units.NgShuttle)
        {
            if (ShuttleLift == NgShuttleLiftState.Down && IsShuttleRaiseAllowed)
                return IsTransferClear
                    ? NgConveyorState.RaisingShuttle : NgConveyorState.WaitingForTransferRelease;
            if (ShuttleLift != NgShuttleLiftState.Down
                && (Position3Occupied && IsAcceptCarrierAllowed(runCommandOn)
                    || !Position3Occupied && ShuttleLift != NgShuttleLiftState.Up))
            {
                if (!IsTransferClear)
                    return NgConveyorState.WaitingForTransferRelease;
                return Position3Occupied
                    ? NgConveyorState.LoweringShuttle : NgConveyorState.RaisingShuttle;
            }
        }
        if (_units.NgConveyor)
            return GetConveyorState(runCommandOn);
        if (_units.NgShuttle && ShuttleLift == NgShuttleLiftState.Down)
            return Position3Occupied || runCommandOn
                ? NgConveyorState.WaitingForShuttleUp : NgConveyorState.CarrierPositionUnknown;
        return NgConveyorState.WaitingForCarrier;
    }

    private NgConveyorState GetConveyorState(bool runCommandOn)
    {
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
                var alarm = _units.NgConveyor && AlarmRequired && _ejectionPhase == EjectionPhase.Idle;
                _io.SetOutput(
                    OutputIo.NgCarrierEjectCompleteLamp,
                    _ejectionPhase == EjectionPhase.WaitingForConfirmation);
                _io.SetOutput(OutputIo.NgCarrierEjectLamp, alarm);

                var state = State;
                TraceStep(state);
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
                        {
                            await WaitForChangeAsync(cancellationToken);
                            break;
                        }

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
                        await WaitForChangeAsync(cancellationToken);
                        break;
                }
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

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.NgShuttleUp or InputIo.NgShuttleDown or InputIo.NgShuttleCarrierDetected
            && ShuttleLift == NgShuttleLiftState.Up && IsShuttleRaiseAllowed)
            _movement = Movement.None;
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
            or InputIo.NgCarrierEjectCompleteButton
            or InputIo.NgShuttleUp
            or InputIo.NgShuttleDown
            or InputIo.NgShuttleCarrierDetected)
        {
            NotifyChanged();
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
}
