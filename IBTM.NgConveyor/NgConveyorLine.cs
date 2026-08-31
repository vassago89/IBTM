using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public enum NgConveyorState
{
    [Description("Waiting for NG Carrier")]
    WaitingForCarrier,

    [Description("Moving Transfer to Carrier")]
    MovingTransferToCarrier,

    [Description("Lowering Transfer at Carrier")]
    LoweringTransferAtCarrier,

    [Description("Closing NG Transfer Gripper")]
    ClosingTransferGripper,

    [Description("Waiting for Carrier Grip")]
    WaitingForCarrierGrip,

    [Description("Raising NG Transfer")]
    RaisingCarrierTransfer,

    [Description("Moving Transfer to Shuttle")]
    MovingTransferToShuttle,

    [Description("Lowering Transfer at Shuttle")]
    LoweringTransferAtShuttle,

    [Description("Opening NG Transfer Gripper")]
    OpeningTransferGripper,

    [Description("Waiting for Shuttle Carrier")]
    WaitingForShuttleCarrier,

    [Description("Lowering NG Shuttle")]
    LoweringShuttle,

    [Description("Moving to Position 1")]
    MovingToPosition1,

    [Description("Moving to Position 2")]
    MovingToPosition2,

    [Description("Shuttle Down / Carrier Position Unknown")]
    CarrierBetweenPositions,

    [Description("Storing at Position 3")]
    StoringAtPosition3,

    [Description("Raising NG Shuttle")]
    RaisingShuttle,

    [Description("NG Conveyor Full")]
    Full,

    [Description("NG Carrier Ready to Eject")]
    ReadyToEject,

    [Description("Ejecting NG Carrier")]
    EjectingCarrier,

    [Description("Securing NG Conveyor Stopper")]
    SecuringEjectStopper,

    [Description("Compacting NG Carriers")]
    CompactingCarriers,

    [Description("Remove Carrier · Press EJECT COMPLETE")]
    WaitingForEjectConfirmation,

    [Description("Acknowledging Eject")]
    AcknowledgingEject,

    [Description("Release EJECT / COMPLETE Buttons")]
    WaitingForEjectButtonRelease,
}

public enum NgShuttleLiftState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}

public sealed class NgConveyorLine
{
    private readonly IIoService _io;
    private readonly StationWork _inspectionWork;
    private readonly NgCarrierTransfer _transfer;
    private readonly NgConveyorSettings _settings;
    private readonly bool _inspectionBypassToNg;
    private volatile NgMovement _movement;
    private volatile EjectionPhase _ejectionPhase;

    public NgConveyorLine(
        IIoService io,
        StationWork inspectionWork,
        NgCarrierTransfer transfer,
        NgConveyorSettings settings,
        bool inspectionBypassToNg)
    {
        _io = io;
        _inspectionWork = inspectionWork;
        _transfer = transfer;
        _settings = settings;
        _inspectionBypassToNg = inspectionBypassToNg;
        io.InputChanged += OnInputChanged;
        inspectionWork.Changed += OnInspectionChanged;
    }

    public event Action? Changed;

    public bool RunCommandOn =>
        _io.GetOutput(OutputIo.NgConveyorRun);
    public int CarrierCount =>
        (Position1Occupied ? 1 : 0)
        + (Position2Occupied ? 1 : 0)
        + (Position3Occupied ? 1 : 0);
    public int AlarmCarrierCount => _settings.AlarmCarrierCount;
    public bool AlarmRequired =>
        CarrierCount >= _settings.AlarmCarrierCount;
    public bool Full =>
        Position1Occupied
        && Position2Occupied
        && Position3Occupied;
    public NgShuttleLiftState ShuttleLift =>
        (ShuttleUp, ShuttleDown) switch
        {
            (true, false) => NgShuttleLiftState.Up,
            (false, true) => NgShuttleLiftState.Down,
            _ => NgShuttleLiftState.Between,
        };

    public NgConveyorState State
    {
        get
        {
            if (_ejectionPhase == EjectionPhase.Ejecting)
            {
                return NgConveyorState.EjectingCarrier;
            }

            if (_ejectionPhase == EjectionPhase.WaitingForConfirmation)
            {
                if (!_io.GetInput(InputIo.NgConveyorStopperUp))
                {
                    return NgConveyorState.SecuringEjectStopper;
                }

                if (NeedsCompaction)
                {
                    return NgConveyorState.CompactingCarriers;
                }

                return _io.GetInput(InputIo.NgCarrierEjectCompleteButton)
                    ? NgConveyorState.AcknowledgingEject
                    : NgConveyorState.WaitingForEjectConfirmation;
            }

            if (_ejectionPhase == EjectionPhase.WaitingForButtonRelease)
            {
                return NgConveyorState.WaitingForEjectButtonRelease;
            }

            if (Position1Occupied
                && _io.GetInput(InputIo.NgCarrierEjectButton)
                && !_transfer.CarrierDetected
                && !ShuttleCarrierDetected
                && _transfer.Lift == NgTransferLiftState.Up)
            {
                return NgConveyorState.EjectingCarrier;
            }

            if (_movement == NgMovement.ToPosition1)
            {
                return NgConveyorState.MovingToPosition1;
            }

            if (_movement == NgMovement.ToPosition2)
            {
                return NgConveyorState.MovingToPosition2;
            }

            if (_movement == NgMovement.RaisingShuttle)
            {
                return NgConveyorState.RaisingShuttle;
            }

            if (ShuttleDown && !ShuttleCarrierDetected)
            {
                return NgConveyorState.CarrierBetweenPositions;
            }

            if (TransferState() is { } transferState)
            {
                return transferState;
            }

            if (ShuttleCarrierDetected)
            {
                return ShuttleDown && Position3Occupied
                    ? TargetState()
                    : NgConveyorState.LoweringShuttle;
            }

            if (!ShuttleUp)
            {
                return NgConveyorState.RaisingShuttle;
            }

            if (NeedsCompaction)
            {
                return _io.GetInput(InputIo.NgConveyorStopperUp)
                    ? NgConveyorState.CompactingCarriers
                    : NgConveyorState.SecuringEjectStopper;
            }

            if (Full)
            {
                return NgConveyorState.Full;
            }

            return Position1Occupied
                ? NgConveyorState.ReadyToEject
                : NgConveyorState.WaitingForCarrier;
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (_ejectionPhase == EjectionPhase.Idle
            && _io.GetInput(InputIo.NgCarrierEjectButton))
        {
            _ejectionPhase = EjectionPhase.WaitingForButtonRelease;
            Changed?.Invoke();
        }

        using var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UpdateOperatorOutputs();
                switch (State)
                {
                    case NgConveyorState.MovingTransferToCarrier:
                        await _transfer.MoveToCarrierAsync(
                            cancellationToken);
                        break;

                    case NgConveyorState.LoweringTransferAtCarrier:
                    case NgConveyorState.LoweringTransferAtShuttle:
                        await _transfer.SetLiftDownAsync(
                            true,
                            cancellationToken);
                        break;

                    case NgConveyorState.ClosingTransferGripper:
                        await _transfer.SetGripperClosedAsync(
                            true,
                            cancellationToken);
                        break;

                    case NgConveyorState.WaitingForCarrierGrip:
                        await _transfer.WaitForCarrierGripAsync(
                            cancellationToken);
                        break;

                    case NgConveyorState.RaisingCarrierTransfer:
                        await _transfer.SetLiftDownAsync(
                            false,
                            cancellationToken);
                        break;

                    case NgConveyorState.MovingTransferToShuttle:
                        await _transfer.MoveToShuttleAsync(
                            cancellationToken);
                        break;

                    case NgConveyorState.OpeningTransferGripper:
                        await _transfer.SetGripperClosedAsync(
                            false,
                            cancellationToken);
                        break;

                    case NgConveyorState.WaitingForShuttleCarrier:
                        await _transfer.WaitForShuttleCarrierAsync(
                            cancellationToken);
                        break;

                    case NgConveyorState.LoweringShuttle:
                        await LowerShuttleAsync(cancellationToken);
                        break;

                    case NgConveyorState.MovingToPosition1:
                        await MoveCarrierAsync(
                            NgMovement.ToPosition1,
                            InputIo.NgConveyorPosition1Occupied,
                            cancellationToken);
                        break;

                    case NgConveyorState.MovingToPosition2:
                        await MoveCarrierAsync(
                            NgMovement.ToPosition2,
                            InputIo.NgConveyorPosition2Occupied,
                            cancellationToken);
                        break;

                    case NgConveyorState.StoringAtPosition3:
                    case NgConveyorState.RaisingShuttle:
                        await RaiseShuttleAsync(cancellationToken);
                        break;

                    case NgConveyorState.EjectingCarrier:
                        await EjectCarrierAsync(cancellationToken);
                        break;

                    case NgConveyorState.SecuringEjectStopper:
                        await _io.SetOutputAndWaitAsync(
                            OutputIo.NgConveyorStopperUp,
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
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, false);
        _io.SetOutput(OutputIo.Buzzer, false);
    }

    private bool Position1Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition1Occupied);
    private bool Position2Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition2Occupied);
    private bool Position3Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition3Occupied);
    private bool NeedsCompaction =>
        !Position1Occupied
        && (Position2Occupied || Position3Occupied);
    private bool ShuttleCarrierDetected =>
        _io.GetInput(InputIo.NgShuttleCarrierDetected);
    private bool ShuttleDown =>
        _io.GetInput(InputIo.NgShuttleDown);
    private bool ShuttleUp =>
        _io.GetInput(InputIo.NgShuttleUp);
    private bool CarrierReadyForNg =>
        _inspectionWork.CanTransfer
        && (_inspectionBypassToNg || _inspectionWork.HasNg);

    private NgConveyorState? TransferState()
    {
        if (_transfer.CarrierDetected)
        {
            if (_transfer.AtShuttle
                && _transfer.Lift == NgTransferLiftState.Down)
            {
                return _transfer.Gripper == NgTransferGripperState.Open
                    ? NgConveyorState.WaitingForShuttleCarrier
                    : NgConveyorState.OpeningTransferGripper;
            }

            if (_transfer.Gripper != NgTransferGripperState.Closed)
            {
                return NgConveyorState.ClosingTransferGripper;
            }

            if (Full)
            {
                return _transfer.Lift == NgTransferLiftState.Up
                    ? NgConveyorState.Full
                    : NgConveyorState.RaisingCarrierTransfer;
            }

            if (_transfer.AtShuttle)
            {
                return NgConveyorState.LoweringTransferAtShuttle;
            }

            return _transfer.Lift == NgTransferLiftState.Up
                ? NgConveyorState.MovingTransferToShuttle
                : NgConveyorState.RaisingCarrierTransfer;
        }

        if (_transfer.AtShuttle
            && _transfer.Lift == NgTransferLiftState.Down
            && _transfer.Gripper == NgTransferGripperState.Open
            && !ShuttleCarrierDetected)
        {
            return NgConveyorState.WaitingForShuttleCarrier;
        }

        if (ShuttleCarrierDetected)
        {
            return _transfer.Lift == NgTransferLiftState.Up
                ? null
                : NgConveyorState.RaisingCarrierTransfer;
        }

        if (!CarrierReadyForNg)
        {
            if (_transfer.Lift != NgTransferLiftState.Up)
            {
                return NgConveyorState.RaisingCarrierTransfer;
            }

            return _transfer.Gripper == NgTransferGripperState.Open
                ? null
                : NgConveyorState.OpeningTransferGripper;
        }

        if (Full)
        {
            return NgConveyorState.Full;
        }

        if (!_transfer.AtCarrier)
        {
            if (_transfer.Lift != NgTransferLiftState.Up)
            {
                return NgConveyorState.RaisingCarrierTransfer;
            }

            return _transfer.Gripper == NgTransferGripperState.Open
                ? NgConveyorState.MovingTransferToCarrier
                : NgConveyorState.OpeningTransferGripper;
        }

        if (_transfer.Lift != NgTransferLiftState.Down)
        {
            return NgConveyorState.LoweringTransferAtCarrier;
        }

        return _transfer.Gripper == NgTransferGripperState.Closed
            ? NgConveyorState.WaitingForCarrierGrip
            : NgConveyorState.ClosingTransferGripper;
    }

    private NgConveyorState TargetState() =>
        !Position1Occupied
            ? NgConveyorState.MovingToPosition1
            : !Position2Occupied
                ? NgConveyorState.MovingToPosition2
                : NgConveyorState.StoringAtPosition3;

    private async Task LowerShuttleAsync(
        CancellationToken cancellationToken)
    {
        await _io.SetOutputAndWaitAsync(
            OutputIo.NgShuttleDown,
            true,
            cancellationToken);
        await _io.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            true,
            cancellationToken);
    }

    private async Task MoveCarrierAsync(
        NgMovement movement,
        InputIo destination,
        CancellationToken cancellationToken)
    {
        _movement = movement;
        await _io.SetOutputAndWaitAsync(
            OutputIo.NgConveyorStopperUp,
            true,
            cancellationToken);
        if (!_io.GetInput(destination))
        {
            StartConveyor();
            try
            {
                await _io.WaitForInputAsync(
                    destination,
                    true,
                    cancellationToken);
            }
            finally
            {
                StopConveyor();
            }
        }

        await RaiseShuttleAsync(cancellationToken);
    }

    private async Task RaiseShuttleAsync(
        CancellationToken cancellationToken)
    {
        _movement = NgMovement.RaisingShuttle;
        await _io.SetOutputAndWaitAsync(
            OutputIo.NgShuttleDown,
            false,
            cancellationToken);
        await _io.WaitForInputAsync(
            InputIo.NgShuttleCarrierDetected,
            false,
            cancellationToken);
        _movement = NgMovement.None;
        Changed?.Invoke();
    }

    private async Task EjectCarrierAsync(
        CancellationToken cancellationToken)
    {
        _ejectionPhase = EjectionPhase.Ejecting;
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, false);
        _io.SetOutput(OutputIo.Buzzer, false);
        if (Position1Occupied)
        {
            await _io.SetOutputAndWaitAsync(
                OutputIo.NgConveyorStopperUp,
                false,
                cancellationToken);
            StartConveyor();
            try
            {
                await _io.WaitForInputAsync(
                    InputIo.NgConveyorPosition1Occupied,
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
        await _io.SetOutputAndWaitAsync(
            OutputIo.NgConveyorStopperUp,
            true,
            cancellationToken);
        _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, true);
    }

    private async Task CompactCarriersAsync(
        CancellationToken cancellationToken)
    {
        StartConveyor();
        try
        {
            await _io.WaitForInputAsync(
                InputIo.NgConveyorPosition1Occupied,
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
        _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, false);
        Changed?.Invoke();
    }

    private void UpdateOperatorOutputs()
    {
        _io.SetOutput(
            OutputIo.NgCarrierEjectCompleteLamp,
            _ejectionPhase == EjectionPhase.WaitingForConfirmation);
        _io.SetOutput(
            OutputIo.NgCarrierEjectLamp,
            AlarmRequired
            && _ejectionPhase == EjectionPhase.Idle);
        _io.SetOutput(
            OutputIo.Buzzer,
            AlarmRequired
            && _ejectionPhase == EjectionPhase.Idle);
    }

    private void StartConveyor()
    {
        _io.SetOutput(OutputIo.NgConveyorReverse, false);
        _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
        _io.SetOutput(OutputIo.NgConveyorRun, true);
    }

    private void StopConveyor() =>
        _io.SetOutput(OutputIo.NgConveyorRun, false);

    private void OnInputChanged(InputIo input, bool _)
    {
        if (_ejectionPhase == EjectionPhase.WaitingForButtonRelease
            && !_io.GetInput(InputIo.NgCarrierEjectButton)
            && !_io.GetInput(InputIo.NgCarrierEjectCompleteButton))
        {
            _ejectionPhase = EjectionPhase.Idle;
        }

        if (input is InputIo.NgCarrierDetected
            or InputIo.NgCarrierGripperClosed
            or InputIo.NgCarrierGripperOpen
            or InputIo.NgShuttleDown
            or InputIo.NgShuttleUp
            or InputIo.NgShuttleCarrierDetected
            or InputIo.NgCarrierPickupDown
            or InputIo.NgCarrierPickupUp
            or InputIo.NgConveyorPosition1Occupied
            or InputIo.NgConveyorPosition2Occupied
            or InputIo.NgConveyorPosition3Occupied
            or InputIo.NgConveyorStopperUp
            or InputIo.NgCarrierEjectButton
            or InputIo.NgCarrierEjectCompleteButton)
        {
            Changed?.Invoke();
        }
    }

    private void OnInspectionChanged() => Changed?.Invoke();

    private enum EjectionPhase
    {
        Idle,
        Ejecting,
        WaitingForConfirmation,
        WaitingForButtonRelease,
    }

    private enum NgMovement
    {
        None,
        ToPosition1,
        ToPosition2,
        RaisingShuttle,
    }
}
