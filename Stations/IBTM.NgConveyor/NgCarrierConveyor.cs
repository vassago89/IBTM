using System;
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
    private int _ejectRequested;
    private int _ejectCompleteRequested;

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
        transfer.Changed += WakeRun;
    }

    public StationCylinderState ShuttleLift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgShuttleUp), _io.GetInput(InputIo.NgShuttleDown)))
            {
                case (true, false):
                    return StationCylinderState.Up;
                case (false, true):
                    return StationCylinderState.Down;
                default:
                    return StationCylinderState.Between;
            }
        }
    }

    public int CarrierCount
    {
        get
        {
            return (_io.GetInput(InputIo.NgConveyorPosition1Occupied) ? 1 : 0)
                + (_io.GetInput(InputIo.NgConveyorPosition2Occupied) ? 1 : 0)
                + (_io.GetInput(InputIo.NgShuttleCarrierDetected) ? 1 : 0);
        }
    }

    public bool AlarmRequired => CarrierCount >= _settings.AlarmCarrierCount;

    public bool Full => CarrierCount == 3;

    // Pending ownership is cleared by the release operation, never by presence DI.
    private bool IsTransferClear
    {
        get
        {
            return _transfer?.IsClear == true
                && _io.GetInput(InputIo.NgCarrierGripperOpen)
                && !_io.GetInput(InputIo.NgCarrierGripperClosed);
        }
    }

    public bool IsReceiveAllowed
    {
        get
        {
            return _units.NgConveyor
                && ShuttleLift == StationCylinderState.Up
                && !_io.GetInput(InputIo.NgShuttleCarrierDetected)
                && IsAcceptCarrierAllowed();
        }
    }

    private bool NeedsCompaction
    {
        get
        {
            return _movement == Movement.Compacting
                || !_io.GetInput(InputIo.NgConveyorPosition1Occupied)
                    && _io.GetInput(InputIo.NgConveyorPosition2Occupied);
        }
    }

    private bool IsAcceptCarrierAllowed(bool? runCommandOn = null)
    {
        return _movement == Movement.None
            && _ejectionPhase == EjectionPhase.Idle
            && Volatile.Read(ref _ejectRequested) == 0
            && !Full
            && !NeedsCompaction
            && (_repeat || !_io.GetInput(InputIo.NgCarrierEjectButton))
            && !(runCommandOn ?? _io.GetOutput(OutputIo.NgConveyorRun));
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        // InputChanged supplies button edges; held buttons and presses during movement
        // must not become another ejection when the current operation ends.
        if (value && IsRunning && _units.NgConveyor && !_repeat)
        {
            if (input == InputIo.NgCarrierEjectButton
                && Step is NgConveyorState.ReadyToEject or NgConveyorState.Full or NgConveyorState.WaitingForEjectConfirmation
                && _ejectionPhase != EjectionPhase.Ejecting
                && Volatile.Read(ref _ejectCompleteRequested) == 0
                && !_io.GetInput(InputIo.NgCarrierEjectCompleteButton)
                && _movement == Movement.None
                && !_io.GetOutput(OutputIo.NgConveyorRun)
                && (_io.GetInput(InputIo.NgConveyorPosition1Occupied)
                    || _io.GetInput(InputIo.NgConveyorPosition2Occupied)))
                Interlocked.Exchange(ref _ejectRequested, 1);
            else if (input == InputIo.NgCarrierEjectCompleteButton
                && _ejectionPhase == EjectionPhase.WaitingForConfirmation
                && Volatile.Read(ref _ejectRequested) == 0
                && !_io.GetInput(InputIo.NgCarrierEjectButton))
                Interlocked.Exchange(ref _ejectCompleteRequested, 1);
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
            NotifyChanged();
        }
    }

    private enum EjectionPhase
    {
        Idle,
        Ejecting,
        WaitingForConfirmation,
    }

    private enum Movement
    {
        None,
        ToPosition1,
        ToPosition2,
        Compacting,
        ReturningToShuttle,
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        _repeat = repeat;
        var motor = new ConveyorRun(_io, OutputIo.NgConveyorRun, cancellationToken, OutputIo.NgCarrierEjectLamp, OutputIo.NgCarrierEjectCompleteLamp);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (repeat && _ejectionPhase != EjectionPhase.Idle)
                throw new InvalidOperationException("Complete NG carrier ejection before starting Repeat.");
            BeginRun();
            _movement = Movement.None;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!repeat && Volatile.Read(ref _ejectRequested) != 0)
                {
                    _ejectionPhase = EjectionPhase.Ejecting;
                    Interlocked.Exchange(ref _ejectRequested, 0);
                    NotifyChanged();
                }
                var state = GetNextStep(_io.GetOutput(OutputIo.NgConveyorRun));
                cancellationToken.ThrowIfCancellationRequested();
                var buttonsEnabled = _units.NgConveyor && !_repeat;
                _io.SetOutput(
                    OutputIo.NgCarrierEjectLamp,
                    buttonsEnabled
                        && state is NgConveyorState.ReadyToEject or NgConveyorState.Full or NgConveyorState.WaitingForEjectConfirmation
                        && Volatile.Read(ref _ejectCompleteRequested) == 0
                        && !_io.GetOutput(OutputIo.NgConveyorRun)
                        && (_io.GetInput(InputIo.NgConveyorPosition1Occupied)
                            || _io.GetInput(InputIo.NgConveyorPosition2Occupied)));
                _io.SetOutput(
                    OutputIo.NgCarrierEjectCompleteLamp,
                    buttonsEnabled && state == NgConveyorState.WaitingForEjectConfirmation);
                switch (state)
                {
                    case NgConveyorState.LoweringShuttle:
                        EnterStep(state);
                        if (Volatile.Read(ref _ejectRequested) != 0)
                            continue;
                        await SetShuttleDownAsync(true, cancellationToken);
                        break;
                    case NgConveyorState.RaisingShuttle:
                        EnterStep(state);
                        await SetShuttleDownAsync(false, cancellationToken);
                        break;
                    case NgConveyorState.MovingToPosition1 or NgConveyorState.MovingToPosition2:
                        EnterStep(state);
                        var toPosition1 = state == NgConveyorState.MovingToPosition1;
                        _movement = toPosition1 ? Movement.ToPosition1 : Movement.ToPosition2;
                        await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true, cancellationToken);
                        await RunUntilAsync(
                            toPosition1 ? InputIo.NgConveyorPosition1Occupied : InputIo.NgConveyorPosition2Occupied,
                            false, cancellationToken);
                        NotifyChanged();
                        break;
                    case NgConveyorState.WaitingForShuttleUp:
                        EnterStep(state, waitingFor:
                            "destination reached; wait for shuttle carrier detection OFF and shuttle UP");
                        if (ShuttleLift != StationCylinderState.Up)
                        {
                            await WaitForChangeAsync(cancellationToken);
                            continue;
                        }

                        _movement = Movement.None;
                        NotifyChanged();
                        break;
                    case NgConveyorState.EjectingCarrier:
                        EnterStep(state, waitingFor:
                            $"S1 then {_settings.EjectRunSeconds:F3} s; S1={_io.GetInput(InputIo.NgConveyorPosition1Occupied)}, S2={_io.GetInput(InputIo.NgConveyorPosition2Occupied)}");
                        if (ShuttleLift != StationCylinderState.Up)
                        {
                            if (!IsTransferClear)
                            {
                                await WaitForChangeAsync(cancellationToken);
                                continue;
                            }
                            await SetShuttleDownAsync(false, cancellationToken);
                        }
                        if (_io.GetInput(InputIo.NgConveyorPosition1Occupied)
                            || _io.GetInput(InputIo.NgConveyorPosition2Occupied))
                        {
                            var duration = TimeSpan.FromSeconds(_settings.EjectRunSeconds);
                            await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, false, cancellationToken);
                            using var arrival = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var atPosition1 = _io.WaitForInputAsync(InputIo.NgConveyorPosition1Occupied, true, arrival.Token);
                            try
                            {
                                StartConveyor(cancellationToken);
                                await atPosition1;
                                await Task.Delay(duration, cancellationToken);
                            }
                            finally
                            {
                                arrival.Cancel();
                                await atPosition1.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                            }
                            _io.SetOutput(OutputIo.NgConveyorRun, false);
                        }
                        //await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true, cancellationToken);
                        _ejectionPhase = EjectionPhase.WaitingForConfirmation;
                        NotifyChanged();
                        break;
                    case NgConveyorState.CompactingCarriers:
                        EnterStep(state);
                        _movement = Movement.Compacting;
                        await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true, cancellationToken);
                        await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, false, cancellationToken);
                        _movement = Movement.None;
                        NotifyChanged();
                        break;
                    case NgConveyorState.WaitingForEjectConfirmation:
                        EnterStep(state);
                        if (Volatile.Read(ref _ejectCompleteRequested) == 0)
                        {
                            await WaitForChangeAsync(cancellationToken);
                            continue;
                        }
                        while (_io.GetInput(InputIo.NgCarrierEjectButton) || _io.GetInput(InputIo.NgCarrierEjectCompleteButton))
                            await WaitForChangeAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!_io.GetInput(InputIo.NgConveyorStopperUp) || _io.GetInput(InputIo.NgConveyorStopperDown))
                            await _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true, cancellationToken);
                        Interlocked.Exchange(ref _ejectCompleteRequested, 0);
                        _ejectionPhase = EjectionPhase.Idle;
                        NotifyChanged();
                        break;
                    case NgConveyorState.WaitingForTransferRelease:
                        EnterStep(state, waitingFor:
                            $"pickup Up={_transfer?.IsRaised}, pending={_transfer?.IsTransferPending}, "
                                + $"gripper Open={_io.GetInput(InputIo.NgCarrierGripperOpen)}, "
                                + $"Closed={_io.GetInput(InputIo.NgCarrierGripperClosed)}");
                        await WaitForChangeAsync(cancellationToken);
                        break;
                    case NgConveyorState.WaitingForShuttleDown:
                        EnterStep(state, waitingFor:
                            $"shuttle={ShuttleLift}, accept={IsAcceptCarrierAllowed()}, movement={_movement}, ejection={_ejectionPhase}");
                        await WaitForChangeAsync(cancellationToken);
                        break;
                    case NgConveyorState.WaitingForCarrier or NgConveyorState.Full
                        or NgConveyorState.ReadyToEject:
                        EnterStep(state);
                        await WaitForChangeAsync(cancellationToken);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported NG conveyor step.");
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
            try
            {
                motor.Dispose();
            }
            finally
            {
                _repeat = false;
                _movement = Movement.None;
                // An interrupted ejection still needs operator confirmation, not an
                // automatic refill on START. This records permission, not carrier position.
                if (_ejectionPhase == EjectionPhase.Ejecting)
                    _ejectionPhase = EjectionPhase.WaitingForConfirmation;
                Interlocked.Exchange(ref _ejectRequested, 0);
                Interlocked.Exchange(ref _ejectCompleteRequested, 0);
                EndRun(cancellationToken);
            }
        }
    }

    internal NgConveyorState GetNextStep(bool runCommandOn)
    {
        if (!_units.NgConveyor)
            return NgConveyorState.WaitingForCarrier;
        switch (_ejectionPhase)
        {
            case EjectionPhase.Ejecting:
                return NgConveyorState.EjectingCarrier;
            case EjectionPhase.WaitingForConfirmation:
                return NgConveyorState.WaitingForEjectConfirmation;
        }

        if (ShuttleLift != StationCylinderState.Up && !runCommandOn)
        {
            var shuttleEmpty = !_io.GetInput(InputIo.NgShuttleCarrierDetected);
            var raiseShuttle = false;
            switch (_movement)
            {
                case Movement.None:
                    // 빈 셔틀을 복귀시키거나, 만재된 셔틀을 벨트에서 분리한다.
                    raiseShuttle = shuttleEmpty || Full;
                    break;
                case Movement.ToPosition1:
                    raiseShuttle = shuttleEmpty && _io.GetInput(InputIo.NgConveyorPosition1Occupied);
                    break;
                case Movement.ToPosition2:
                    raiseShuttle = shuttleEmpty && _io.GetInput(InputIo.NgConveyorPosition2Occupied);
                    break;
            }
            if (raiseShuttle)
                return IsTransferClear
                    ? NgConveyorState.RaisingShuttle : NgConveyorState.WaitingForTransferRelease;
        }
        if (_io.GetInput(InputIo.NgShuttleCarrierDetected) && IsAcceptCarrierAllowed(runCommandOn))
        {
            if (!IsTransferClear)
                return NgConveyorState.WaitingForTransferRelease;
            if (ShuttleLift != StationCylinderState.Down)
                return NgConveyorState.LoweringShuttle;
        }
        switch (_movement)
        {
            case Movement.ToPosition1:
                return _io.GetInput(InputIo.NgConveyorPosition1Occupied)
                    ? NgConveyorState.WaitingForShuttleUp
                    : NgConveyorState.MovingToPosition1;
            case Movement.ToPosition2:
                return _io.GetInput(InputIo.NgConveyorPosition2Occupied)
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
        if (ShuttleLift != StationCylinderState.Down)
            return NgConveyorState.WaitingForShuttleDown;
        if (!_io.GetInput(InputIo.NgConveyorPosition1Occupied))
            return NgConveyorState.MovingToPosition1;
        return !_io.GetInput(InputIo.NgConveyorPosition2Occupied) ? NgConveyorState.MovingToPosition2 : NgConveyorState.Full;
    }

    public Task SetShuttleDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (down && (_ejectionPhase != EjectionPhase.Idle || Volatile.Read(ref _ejectRequested) != 0))
            throw new MotionInterlockException("Complete NG carrier ejection before lowering the shuttle.");
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
    }

    public void Stop()
    {
        using var motor = new ConveyorRun(
            _io, OutputIo.NgConveyorRun, CancellationToken.None,
            OutputIo.NgCarrierEjectLamp, OutputIo.NgCarrierEjectCompleteLamp);
    }

    internal async Task RunUntilAsync(
        InputIo destination,
        bool reverse,
        CancellationToken cancellationToken)
    {
        if (_io.GetInput(destination))
        {
            return;
        }

        using var motor = new ConveyorRun(_io, OutputIo.NgConveyorRun, cancellationToken);
        using var arrival = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var arrived = _io.WaitForInputAsync(destination, true, arrival.Token);
        try
        {
            StartConveyor(cancellationToken, reverse);
            await arrived;
            await Task.Delay(5000, cancellationToken);
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
        finally
        {
            arrival.Cancel();
            await arrived.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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
