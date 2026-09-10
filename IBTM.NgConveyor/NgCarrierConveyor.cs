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

    internal bool StopperUp
    {
        get
        {
            return _io.GetInput(InputIo.NgConveyorStopperUp);
        }
    }

    internal bool StopperDown
    {
        get
        {
            return _io.GetInput(InputIo.NgConveyorStopperDown);
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

    internal bool CarrierMoving
    {
        get
        {
            return _movement is Movement.ToPosition1 or Movement.ToPosition2 or Movement.Compacting;
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
                && !EjectRequested
                && !RunCommandOn;
        }
    }

    internal bool ShuttleCanRaise
    {
        get
        {
            return _movement == Movement.WaitingForShuttleRaise
                || State == NgConveyorState.Full;
        }
    }

    public NgConveyorState State
    {
        get
        {
            switch (_ejectionPhase)
            {
                case EjectionPhase.Ejecting:
                    return NgConveyorState.EjectingCarrier;
                case EjectionPhase.WaitingForConfirmation:
                    if (!StopperUp)
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
                && EjectRequested
                && _shuttle.Lift == NgShuttleLiftState.Up)
            {
                return NgConveyorState.EjectingCarrier;
            }

            switch (_movement)
            {
                case Movement.ToPosition1:
                    return NgConveyorState.MovingToPosition1;
                case Movement.ToPosition2:
                    return NgConveyorState.MovingToPosition2;
                case Movement.Compacting:
                    return StopperUp
                        ? NgConveyorState.CompactingCarriers
                        : NgConveyorState.SecuringEjectStopper;
                case Movement.WaitingForShuttleRaise:
                    return NgConveyorState.WaitingForShuttleUp;
            }

            if (NeedsCompaction)
            {
                return StopperUp
                    ? NgConveyorState.CompactingCarriers
                    : NgConveyorState.SecuringEjectStopper;
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
        using var stopRegistration = cancellationToken.Register(StopConveyor);
        try
        {
            StartConveyor(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StopConveyor();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stopRegistration = cancellationToken.Register(StopConveyor);
        if (_ejectionPhase == EjectionPhase.Idle && EjectRequested)
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
        }
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
                return SetStopperUpAsync(true, cancellationToken);
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
        SetBuzzer(false);
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
        await SetStopperUpAsync(true, cancellationToken);
        await RunUntilAsync(PositionInput(destination), true, false, cancellationToken);

        _movement = Movement.WaitingForShuttleRaise;
        Changed?.Invoke();
    }

    private async Task EjectCarrierAsync(CancellationToken cancellationToken)
    {
        _ejectionPhase = EjectionPhase.Ejecting;
        SetEjectLamp(false);
        SetBuzzer(false);
        if (Position1Occupied)
        {
            await SetStopperUpAsync(false, cancellationToken);
            await RunUntilAsync(InputIo.NgConveyorPosition1Occupied, false, false, cancellationToken);
        }

        _ejectionPhase = EjectionPhase.WaitingForConfirmation;
        Changed?.Invoke();
        await SetStopperUpAsync(true, cancellationToken);
        SetEjectCompleteLamp(true);
    }

    private async Task CompactCarriersAsync(CancellationToken cancellationToken)
    {
        _movement = Movement.Compacting;
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
        SetBuzzer(alarm);
    }

    internal Task SetStopperUpAsync(bool up, CancellationToken cancellationToken)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, up, cancellationToken);
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
        _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
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

    private void SetBuzzer(bool on)
    {
        _io.SetOutput(OutputIo.Buzzer, on);
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
            && _movement == Movement.WaitingForShuttleRaise)
        {
            _movement = Movement.None;
        }

        NotifyChanged();
    }

    private void OnInputChanged(InputIo input, bool _)
    {
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
        WaitingForShuttleRaise,
    }
}
