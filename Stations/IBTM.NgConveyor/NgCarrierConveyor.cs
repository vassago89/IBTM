using System;
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

    internal bool CanAcceptCarrier
    {
        get
        {
            return _movement == Movement.None
                && _ejectionPhase == EjectionPhase.Idle
                && !Full
                && !NeedsCompaction
                && (_repeat || !EjectRequested)
                && !RunCommandOn;
        }
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
            // A stopped transfer with no presence feedback has no known physical location.
            // The saved destination is work history, not permission to guess and resume.
            if (!RunCommandOn
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

                return _shuttle.Lift == NgShuttleLiftState.Down
                    ? TargetState()
                    : NgConveyorState.WaitingForShuttleDown;
            }

            return Position1Occupied ? NgConveyorState.ReadyToEject : NgConveyorState.WaitingForCarrier;
        }
    }

    public async Task RunMotorAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var stopRegistration = cancellationToken.Register(StopConveyor);
            StartConveyor(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Stop();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        _repeat = repeat;
        using var stopRegistration = cancellationToken.Register(StopConveyor);
        if (!repeat && _ejectionPhase == EjectionPhase.Idle && EjectRequested)
        {
            _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
            Changed?.Invoke();
        }

        try
        {
            await RunLoopAsync(ExecuteAsync, cancellationToken);
        }
        finally
        {
            Stop();
            _repeat = false;
        }
    }

    public async Task ReturnToShuttleAsync(CancellationToken cancellationToken)
    {
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
        switch (State)
        {
            case NgConveyorState.MovingToPosition1:
                return MoveCarrierAsync(
                    NgConveyorPosition.Position1,
                    Movement.ToPosition1,
                    cancellationToken);
            case NgConveyorState.MovingToPosition2:
                return MoveCarrierAsync(
                    NgConveyorPosition.Position2,
                    Movement.ToPosition2,
                    cancellationToken);
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
                AcknowledgeEject();
                break;
            default:
                return WaitForChangeAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        StopConveyor();
        SetEjectLamp(false);
        SetEjectCompleteLamp(false);
    }

    private bool NeedsCompaction
    {
        get
        {
            return _movement == Movement.Compacting || !Position1Occupied && Position2Occupied;
        }
    }

    private NgConveyorState TargetState()
    {
        if (!Position1Occupied)
        {
            return NgConveyorState.MovingToPosition1;
        }

        return !Position2Occupied ? NgConveyorState.MovingToPosition2 : NgConveyorState.Full;
    }

    private async Task MoveCarrierAsync(
        NgConveyorPosition destination,
        Movement movement,
        CancellationToken cancellationToken)
    {
        _movement = movement;
        await SetStopperDownAsync(false, cancellationToken);
        await RunUntilAsync(PositionInput(destination), true, false, cancellationToken);

        Changed?.Invoke();
    }

    private async Task EjectCarrierAsync(CancellationToken cancellationToken)
    {
        _ejectionPhase = EjectionPhase.Ejecting;
        SetEjectLamp(false);
        if (Position1Occupied)
        {
            await SetStopperDownAsync(true, cancellationToken);
            await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, false, false, cancellationToken);
        }

        await SetStopperDownAsync(false, cancellationToken);
        _ejectionPhase = EjectionPhase.WaitingForConfirmation;
        Changed?.Invoke();
        SetEjectCompleteLamp(true);
    }

    private async Task CompactCarriersAsync(CancellationToken cancellationToken)
    {
        _movement = Movement.Compacting;
        await SetStopperDownAsync(false, cancellationToken);
        await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, true, false, cancellationToken);
        _movement = Movement.None;
        Changed?.Invoke();
    }

    private void AcknowledgeEject()
    {
        _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
        SetEjectCompleteLamp(false);
        Changed?.Invoke();
    }

    private void UpdateOperatorOutputs()
    {
        var alarm = AlarmRequired && _ejectionPhase == EjectionPhase.Idle;
        SetEjectCompleteLamp(_ejectionPhase == EjectionPhase.WaitingForConfirmation);
        SetEjectLamp(alarm);
    }

    private Task SetStopperDownAsync(bool down, CancellationToken cancellationToken)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperDown, down, cancellationToken);
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

        try
        {
            StartConveyor(cancellationToken, reverse);
            await _io.WaitForInputAsync(destination, occupied, cancellationToken);
        }
        finally
        {
            StopConveyor();
        }
    }

    private static InputIo PositionInput(NgConveyorPosition position)
    {
        return position switch
        {
            NgConveyorPosition.Position1 => InputIo.NgConveyorPosition1Occupied,
            NgConveyorPosition.Position2 => InputIo.NgConveyorPosition2Occupied,
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };
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

    private void SetEjectLamp(bool on)
    {
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, on);
    }

    private void SetEjectCompleteLamp(bool on)
    {
        _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, on);
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
