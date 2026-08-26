using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.NgConveyor;

public enum NgConveyorState
{
    [Description("Waiting for NG Carrier Jig")]
    WaitingForCarrier,

    [Description("Picking NG Carrier Jig")]
    PickingCarrier,

    [Description("Placing on NG Shuttle")]
    PlacingOnShuttle,

    [Description("Lowering NG Shuttle")]
    LoweringShuttle,

    [Description("Moving to Position 1")]
    MovingToPosition1,

    [Description("Moving to Position 2")]
    MovingToPosition2,

    [Description("Storing at Position 3")]
    StoringAtPosition3,

    [Description("Raising NG Shuttle")]
    RaisingShuttle,

    [Description("NG Conveyor Full")]
    Full,

    [Description("NG Carrier Jig Ready to Eject")]
    ReadyToEject,

    [Description("Ejecting NG Carrier Jig")]
    EjectingCarrier,

    [Description("Waiting for Eject Confirmation")]
    WaitingForEjectConfirmation,
}

public sealed class NgConveyorLine
{
    private readonly IIoService _io;
    private readonly InspectionWork _inspectionWork;
    private readonly NgCarrierTransfer _transfer;

    public NgConveyorLine(
        IIoService io,
        InspectionWork inspectionWork,
        NgCarrierTransfer transfer)
    {
        _io = io;
        _inspectionWork = inspectionWork;
        _transfer = transfer;
        io.InputChanged += OnInputChanged;
        inspectionWork.Changed += OnInspectionChanged;
    }

    public event Action? Changed;

    public bool RunCommandOn =>
        _io.GetOutput(OutputIo.NgConveyorRun);
    public bool Full =>
        Position1Occupied
        && Position2Occupied
        && Position3Occupied;

    public NgConveyorState State
    {
        get
        {
            if (_io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp))
            {
                return NgConveyorState.WaitingForEjectConfirmation;
            }

            if (ShuttleCarrierDetected)
            {
                return ShuttleDown
                    ? TargetState()
                    : NgConveyorState.LoweringShuttle;
            }

            if (!ShuttleUp)
            {
                return NgConveyorState.RaisingShuttle;
            }

            if (_transfer.HoldingCarrier)
            {
                return Full
                    ? NgConveyorState.Full
                    : NgConveyorState.PlacingOnShuttle;
            }

            if (Position1Occupied
                && _io.GetInput(InputIo.NgCarrierEjectButton))
            {
                return NgConveyorState.EjectingCarrier;
            }

            if (_inspectionWork.CanTransfer && _inspectionWork.HasNg)
            {
                return Full
                    ? NgConveyorState.Full
                    : NgConveyorState.PickingCarrier;
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
        using var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                switch (State)
                {
                    case NgConveyorState.PickingCarrier:
                        await _transfer.PickAsync(cancellationToken);
                        break;

                    case NgConveyorState.PlacingOnShuttle:
                        await _transfer.PlaceOnShuttleAsync(cancellationToken);
                        break;

                    case NgConveyorState.LoweringShuttle:
                        await LowerShuttleAsync(cancellationToken);
                        break;

                    case NgConveyorState.MovingToPosition1:
                        await MoveCarrierAsync(
                            InputIo.NgConveyorPosition1Occupied,
                            cancellationToken);
                        break;

                    case NgConveyorState.MovingToPosition2:
                        await MoveCarrierAsync(
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

                    default:
                        UpdateEjectLamp();
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
        _io.SetOutput(OutputIo.NgConveyorRun, false);
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, false);
        _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, false);
    }

    private bool Position1Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition1Occupied);
    private bool Position2Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition2Occupied);
    private bool Position3Occupied =>
        _io.GetInput(InputIo.NgConveyorPosition3Occupied);
    private bool ShuttleCarrierDetected =>
        _io.GetInput(InputIo.NgShuttleCarrierDetected);
    private bool ShuttleDown =>
        _io.GetInput(InputIo.NgShuttleDown);
    private bool ShuttleUp =>
        _io.GetInput(InputIo.NgShuttleUp);

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
        InputIo destination,
        CancellationToken cancellationToken)
    {
        await _io.SetOutputAndWaitAsync(
            OutputIo.NgConveyorStopperUp,
            true,
            cancellationToken);
        _io.SetOutput(OutputIo.NgConveyorReverse, false);
        _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
        _io.SetOutput(OutputIo.NgConveyorRun, true);
        try
        {
            await _io.WaitForInputAsync(
                destination,
                true,
                cancellationToken);
        }
        finally
        {
            Stop();
        }

        await RaiseShuttleAsync(cancellationToken);
    }

    private async Task RaiseShuttleAsync(
        CancellationToken cancellationToken)
    {
        await _io.SetOutputAndWaitAsync(
            OutputIo.NgShuttleDown,
            false,
            cancellationToken);
        await _io.WaitForInputAsync(
            InputIo.NgShuttleCarrierDetected,
            false,
            cancellationToken);
    }

    private async Task EjectCarrierAsync(
        CancellationToken cancellationToken)
    {
        var compactRemainingCarriers =
            Position2Occupied || Position3Occupied;
        _io.SetOutput(OutputIo.NgCarrierEjectLamp, false);
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

        await _io.SetOutputAndWaitAsync(
            OutputIo.NgConveyorStopperUp,
            true,
            cancellationToken);
        if (compactRemainingCarriers)
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

        _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, true);
        await WaitForOperatorInputAsync(
            InputIo.NgCarrierEjectCompleteButton,
            true,
            cancellationToken);
        _io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, false);
        await WaitForOperatorInputAsync(
            InputIo.NgCarrierEjectButton,
            false,
            cancellationToken);
        await WaitForOperatorInputAsync(
            InputIo.NgCarrierEjectCompleteButton,
            false,
            cancellationToken);
    }

    private void UpdateEjectLamp() =>
        _io.SetOutput(
            OutputIo.NgCarrierEjectLamp,
            Position1Occupied);

    private void StartConveyor()
    {
        _io.SetOutput(OutputIo.NgConveyorReverse, false);
        _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
        _io.SetOutput(OutputIo.NgConveyorRun, true);
    }

    private void StopConveyor() =>
        _io.SetOutput(OutputIo.NgConveyorRun, false);

    private async Task WaitForOperatorInputAsync(
        InputIo input,
        bool value,
        CancellationToken cancellationToken)
    {
        using var changed = new AsyncAutoResetEvent();
        void OnInputChanged(InputIo changedInput, bool changedValue)
        {
            if (changedInput == input && changedValue == value)
            {
                changed.Set();
            }
        }

        _io.InputChanged += OnInputChanged;
        try
        {
            while (_io.GetInput(input) != value)
            {
                await changed.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            _io.InputChanged -= OnInputChanged;
        }
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.NgCarrierJigDetected
            or InputIo.NgShuttleDown
            or InputIo.NgShuttleUp
            or InputIo.NgShuttleCarrierDetected
            or InputIo.NgConveyorPosition1Occupied
            or InputIo.NgConveyorPosition2Occupied
            or InputIo.NgConveyorPosition3Occupied
            or InputIo.NgCarrierEjectButton
            or InputIo.NgCarrierEjectCompleteButton)
        {
            Changed?.Invoke();
        }
    }

    private void OnInspectionChanged() => Changed?.Invoke();
}
