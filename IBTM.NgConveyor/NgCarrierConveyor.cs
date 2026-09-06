using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgCarrierConveyor
{
    private readonly IIoService _io;
    private readonly NgConveyorSettings _settings;
    private readonly NgShuttleFeedback _shuttle;
    private volatile Movement _movement;
    private volatile EjectionPhase _ejectionPhase;

    public NgCarrierConveyor(
        IIoService io,
        NgConveyorSettings settings,
        NgShuttleFeedback shuttle)
    {
        _io = io;
        _settings = settings;
        _shuttle = shuttle;
        shuttle.Changed += OnShuttleChanged;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
    }

    public event Action? Changed;

    public bool RunCommandOn =>
        _io.GetOutput(OutputIo.NgConveyorRun);
    public bool Position1Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition1Occupied);
    public bool Position2Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition2Occupied);
    public bool Position3Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition3Occupied);
    public int CarrierCount =>
        (Position1Occupied ? 1 : 0)
        + (Position2Occupied ? 1 : 0)
        + (Position3Occupied ? 1 : 0);
    public int AlarmCarrierCount => _settings.AlarmCarrierCount;
    public bool AlarmRequired => CarrierCount >= AlarmCarrierCount;
    public bool Full => CarrierCount == 3;
    private bool StopperUp =>
        _io.GetInput(InputIo.NgConveyorStopperUp);
    private bool EjectRequested =>
        _io.GetInput(InputIo.NgCarrierEjectButton);
    private bool EjectConfirmed =>
        _io.GetInput(InputIo.NgCarrierEjectCompleteButton);
    internal bool CarrierMoving =>
        _movement is Movement.ToPosition1 or Movement.ToPosition2;
    internal bool CanAcceptCarrier =>
        _movement == Movement.None
        && _ejectionPhase == EjectionPhase.Idle
        && !Position3Occupied
        && !NeedsCompaction
        && !EjectRequested
        && !RunCommandOn;
    internal bool ShuttleCanRaise =>
        _movement == Movement.WaitingForShuttleRaise
        || State == NgConveyorState.Full;

    internal Task WaitForPosition3Async(
        CancellationToken cancellationToken) =>
        WaitForPositionAsync(
            NgConveyorPosition.Position3,
            true,
            cancellationToken);

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
                && _shuttle.Lift == NgShuttleLiftState.Up
                && !_shuttle.CarrierDetected)
            {
                return NgConveyorState.EjectingCarrier;
            }

            switch (_movement)
            {
                case Movement.ToPosition1:
                    return NgConveyorState.MovingToPosition1;
                case Movement.ToPosition2:
                    return NgConveyorState.MovingToPosition2;
                case Movement.WaitingForShuttleRaise:
                    return NgConveyorState.WaitingForShuttleUp;
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

            if (NeedsCompaction)
            {
                return StopperUp
                    ? NgConveyorState.CompactingCarriers
                    : NgConveyorState.SecuringEjectStopper;
            }

            return Position1Occupied
                ? NgConveyorState.ReadyToEject
                : NgConveyorState.WaitingForCarrier;
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        using var stopRegistration = cancellationToken.Register(StopConveyor);
        if (_ejectionPhase == EjectionPhase.Idle
            && EjectRequested)
        {
            _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
            Changed?.Invoke();
        }

        var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UpdateOperatorOutputs();
                switch (State)
                {
                    case NgConveyorState.MovingToPosition1:
                        await MoveCarrierAsync(
                            NgConveyorPosition.Position1,
                            Movement.ToPosition1,
                            cancellationToken);
                        break;
                    case NgConveyorState.MovingToPosition2:
                        await MoveCarrierAsync(
                            NgConveyorPosition.Position2,
                            Movement.ToPosition2,
                            cancellationToken);
                        break;
                    case NgConveyorState.WaitingForShuttleUp:
                        if (_shuttle.Lift == NgShuttleLiftState.Up)
                        {
                            _movement = Movement.None;
                            Changed?.Invoke();
                        }
                        else
                        {
                            await stateChanged.WaitAsync(cancellationToken);
                        }
                        break;
                    case NgConveyorState.EjectingCarrier:
                        await EjectCarrierAsync(cancellationToken);
                        break;
                    case NgConveyorState.SecuringEjectStopper:
                        await SetStopperUpAsync(
                            true,
                            cancellationToken);
                        break;
                    case NgConveyorState.CompactingCarriers:
                        await CompactCarriersAsync(cancellationToken);
                        break;
                    case NgConveyorState.AcknowledgingEject:
                        AcknowledgeEject();
                        break;
                    default:
                        await stateChanged.WaitAsync(cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Changed -= OnStateChanged;
            Stop();
        }
    }

    public void Stop()
    {
        StopConveyor();
        SetEjectLamp(false);
        SetEjectCompleteLamp(false);
        SetBuzzer(false);
    }

    private bool NeedsCompaction =>
        !Position1Occupied && Position2Occupied;

    private NgConveyorState TargetState()
    {
        if (!Position1Occupied)
        {
            return NgConveyorState.MovingToPosition1;
        }

        return !Position2Occupied
            ? NgConveyorState.MovingToPosition2
            : NgConveyorState.Full;
    }

    private async Task MoveCarrierAsync(
        NgConveyorPosition destination,
        Movement movement,
        CancellationToken cancellationToken)
    {
        _movement = movement;
        await SetStopperUpAsync(true, cancellationToken);
        try
        {
            StartConveyor(cancellationToken);
            await WaitForPositionAsync(
                destination,
                true,
                cancellationToken);
        }
        finally
        {
            StopConveyor();
        }

        _movement = Movement.WaitingForShuttleRaise;
        Changed?.Invoke();
    }

    private async Task EjectCarrierAsync(
        CancellationToken cancellationToken)
    {
        _ejectionPhase = EjectionPhase.Ejecting;
        SetEjectLamp(false);
        SetBuzzer(false);
        if (Position1Occupied)
        {
            await SetStopperUpAsync(false, cancellationToken);
            try
            {
                StartConveyor(cancellationToken);
                await WaitForPositionAsync(
                    NgConveyorPosition.Position1,
                    false,
                    cancellationToken);
            }
            finally
            {
                StopConveyor();
            }
        }

        _ejectionPhase = EjectionPhase.WaitingForConfirmation;
        Changed?.Invoke();
        await SetStopperUpAsync(true, cancellationToken);
        SetEjectCompleteLamp(true);
    }

    private async Task CompactCarriersAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            StartConveyor(cancellationToken);
            await WaitForPositionAsync(
                NgConveyorPosition.Position1,
                true,
                cancellationToken);
        }
        finally
        {
            StopConveyor();
        }
    }

    private void AcknowledgeEject()
    {
        _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
        SetEjectCompleteLamp(false);
        Changed?.Invoke();
    }

    private void UpdateOperatorOutputs()
    {
        var alarm = AlarmRequired
                    && _ejectionPhase == EjectionPhase.Idle;
        SetEjectCompleteLamp(
            _ejectionPhase == EjectionPhase.WaitingForConfirmation);
        SetEjectLamp(alarm);
        SetBuzzer(alarm);
    }

    private Task SetStopperUpAsync(
        bool up,
        CancellationToken cancellationToken) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.NgConveyorStopperUp,
            up,
            cancellationToken);

    private Task WaitForPositionAsync(
        NgConveyorPosition position,
        bool occupied,
        CancellationToken cancellationToken) =>
        _io.WaitForInputAsync(
            position switch
            {
                NgConveyorPosition.Position1 =>
                    InputIo.NgConveyorPosition1Occupied,
                NgConveyorPosition.Position2 =>
                    InputIo.NgConveyorPosition2Occupied,
                NgConveyorPosition.Position3 =>
                    InputIo.NgConveyorPosition3Occupied,
                _ => throw new ArgumentOutOfRangeException(nameof(position)),
            },
            occupied,
            cancellationToken);

    private void StartConveyor(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorReverse, false);
        _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorRun, true);
    }

    private void StopConveyor() =>
        _io.SetOutput(OutputIo.NgConveyorRun, false);

    private void SetEjectLamp(bool on) =>
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, on);

    private void SetEjectCompleteLamp(bool on) =>
        _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, on);

    private void SetBuzzer(bool on) =>
        _io.SetOutput(OutputIo.Buzzer, on);

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
            or InputIo.NgConveyorPosition3Occupied
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
        WaitingForShuttleRaise,
    }
}
