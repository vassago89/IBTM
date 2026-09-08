using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed class MainConveyor : AutoUnit
{
    private readonly IIoService _io;
    private readonly OperationCancellation _operations;
    private readonly StationWork _placementWork;
    private readonly StationWork _boltFasteningWork;
    private readonly StationWork _inspectionWork;
    private readonly ConveyorStation _placement;
    private readonly ConveyorStation _boltFastening;
    private readonly ConveyorStation _inspection;
    private readonly Func<bool> _routeInspectionToNg;
    private OperationCancellation.Operation? _automaticCancellation;
    private OperationCancellation.Operation? _manualCancellation;
    private CancellationTokenRegistration _manualStopRegistration;
    private volatile ConveyorTransfer _transfer;

    public MainConveyor(
        IIoService io,
        OperationCancellation operations,
        StationWork placementWork,
        StationWork boltFasteningWork,
        StationWork inspectionWork,
        Func<bool> routeInspectionToNg)
    {
        _io = io;
        _operations = operations;
        _placementWork = placementWork;
        _boltFasteningWork = boltFasteningWork;
        _inspectionWork = inspectionWork;
        _placement = placementWork.Station;
        _boltFastening = boltFasteningWork.Station;
        _inspection = inspectionWork.Station;
        _routeInspectionToNg = routeInspectionToNg;
        io.InputChanged += OnInputChanged;
        placementWork.Changed += NotifyChanged;
        boltFasteningWork.Changed += NotifyChanged;
        inspectionWork.Changed += NotifyChanged;
        _placement.CarrierChanged += value =>
            OnCarrierChanged(StationPosition.PcbPlacement, value);
        _boltFastening.CarrierChanged += value =>
            OnCarrierChanged(StationPosition.BoltFastening, value);
        _inspection.CarrierChanged += value =>
            OnCarrierChanged(StationPosition.Inspection, value);
    }

    public override event Action? Changed;
    public bool RunCommandOn => _io.GetOutput(OutputIo.MainConveyorRun);
    public bool EntryCarrierDetected =>
        _io.GetInput(InputIo.MainConveyorEntryCarrierDetected);
    public bool ExitCarrierDetected =>
        _io.GetInput(InputIo.MainConveyorExitCarrierDetected);

    public MainConveyorState State
    {
        get
        {
            if (ExitCarrierDetected)
            {
                return _io.GetInput(InputIo.MainConveyorReadyFromRear)
                    ? MainConveyorState.DischargingInspectionCarrier
                    : MainConveyorState.WaitingForRearEquipment;
            }

            if (_placementWork.CanReceive && EntryCarrierDetected)
            {
                return MainConveyorState.ReceivingFrontCarrier;
            }

            switch (_transfer)
            {
                case ConveyorTransfer.DischargingInspectionFromExit:
                    return MainConveyorState.DischargingInspectionCarrier;

                case ConveyorTransfer.DischargingInspectionToExit:
                    return _io.GetInput(InputIo.MainConveyorReadyFromRear)
                        ? MainConveyorState.DischargingInspectionCarrier
                        : MainConveyorState.WaitingForRearEquipment;

                case ConveyorTransfer.ReceivingBeforeEntry:
                case ConveyorTransfer.ReceivingAfterEntry:
                    return MainConveyorState.ReceivingFrontCarrier;

                case ConveyorTransfer.BoltFasteningToInspection:
                    return MainConveyorState.MovingBoltFasteningToInspection;

                case ConveyorTransfer.PcbPlacementToBoltFastening:
                    return MainConveyorState.MovingPcbPlacementToBoltFastening;
            }

            if (_inspectionWork.CarrierPresent
                && !_inspectionWork.CarrierSeated)
            {
                return MainConveyorState.SeatingInspectionCarrier;
            }

            if (_boltFasteningWork.CarrierPresent
                && !_boltFasteningWork.CarrierSeated)
            {
                return MainConveyorState.SeatingBoltFasteningCarrier;
            }

            if (_placementWork.CarrierPresent
                && !_placementWork.CarrierSeated)
            {
                return MainConveyorState.SeatingPcbPlacementCarrier;
            }

            if (CanDischargeInspection)
            {
                return MainConveyorState.DischargingInspectionCarrier;
            }

            if (CanMoveBoltFasteningToInspection)
            {
                return MainConveyorState.MovingBoltFasteningToInspection;
            }

            if (CanMovePlacementToBoltFastening)
            {
                return MainConveyorState.MovingPcbPlacementToBoltFastening;
            }

            if (CanReceiveAtPlacement)
            {
                return MainConveyorState.ReceivingFrontCarrier;
            }

            if (CanOfferToRear)
            {
                return MainConveyorState.WaitingForRearEquipment;
            }

            return _placementWork.CarrierPresent
                ? MainConveyorState.Idle
                : MainConveyorState.WaitingForFrontCarrier;
        }
    }

    public void RunMotor(CancellationToken cancellationToken = default)
    {
        Stop();
        _manualCancellation = _operations.Link(cancellationToken);
        try
        {
            var runToken = _manualCancellation.Token;
            runToken.ThrowIfCancellationRequested();
            _manualStopRegistration = runToken.Register(StopMotor);
            StartMotor(runToken);
            runToken.ThrowIfCancellationRequested();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        Stop();
        using var runCancellation = _operations.Link(cancellationToken);
        _automaticCancellation = runCancellation;
        cancellationToken = runCancellation.Token;
        using var stopRegistration = cancellationToken.Register(StopMotor);
        try
        {
            await RunLoopAsync(ExecuteAsync, cancellationToken);
        }
        finally
        {
            ResetSmema();
            StopMotor();
            if (ReferenceEquals(_automaticCancellation, runCancellation))
            {
                _automaticCancellation = null;
            }
        }
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        switch (State)
        {
            case MainConveyorState.SeatingInspectionCarrier:
                return _inspection.SeatAsync(cancellationToken);
            case MainConveyorState.SeatingBoltFasteningCarrier:
                return _boltFastening.SeatAsync(cancellationToken);
            case MainConveyorState.SeatingPcbPlacementCarrier:
                return _placement.SeatAsync(cancellationToken);
            case MainConveyorState.DischargingInspectionCarrier:
                return DischargeInspectionAsync(cancellationToken);
            case MainConveyorState.MovingBoltFasteningToInspection:
                return MoveCarrierAsync(
                    ConveyorTransfer.BoltFasteningToInspection,
                    _boltFastening, _inspection, cancellationToken);
            case MainConveyorState.MovingPcbPlacementToBoltFastening:
                return MoveCarrierAsync(
                    ConveyorTransfer.PcbPlacementToBoltFastening,
                    _placement, _boltFastening, cancellationToken);
            case MainConveyorState.ReceivingFrontCarrier:
                return ReceiveAtPlacementAsync(cancellationToken);
            default:
                UpdateSmema();
                return WaitForChangeAsync(cancellationToken);
        }
    }

    public void Stop()
    {
        var manual = _manualCancellation;
        _manualCancellation = null;
        try
        {
            _automaticCancellation?.Cancel();
            manual?.Cancel();
        }
        finally
        {
            try
            {
                _manualStopRegistration.Dispose();
                StopMotor();
                ResetSmema();
            }
            finally
            {
                manual?.Dispose();
            }
        }
    }

    private void ResetSmema()
    {
        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
    }

    private void StopMotor() =>
        _io.SetOutput(OutputIo.MainConveyorRun, false);

    private bool CanOfferToRear =>
        ExitCarrierDetected
            || InspectionDischargeActive
            || !_routeInspectionToNg()
            && _inspectionWork.CanTransfer;

    private bool CanDischargeInspection =>
        CanOfferToRear
        && _io.GetInput(InputIo.MainConveyorReadyFromRear);

    private bool CanMoveBoltFasteningToInspection =>
        _boltFasteningWork.CanTransfer
        && _inspectionWork.CanReceive;

    private bool CanMovePlacementToBoltFastening =>
        _placementWork.CanTransfer
        && _boltFasteningWork.CanReceive;

    private bool CanReceiveAtPlacement =>
        _placementWork.CanReceive
        && (_io.GetInput(InputIo.MainConveyorAvailableFromFront2)
            || EntryCarrierDetected);

    private bool InspectionDischargeActive =>
        _transfer is ConveyorTransfer.DischargingInspectionToExit
            or ConveyorTransfer.DischargingInspectionFromExit;

    private void UpdateSmema()
    {
        var rearAvailable = CanOfferToRear;
        _io.SetOutput(
            OutputIo.MainConveyorReadyToFront2,
            _placementWork.CanReceive && !rearAvailable);
        _io.SetOutput(
            OutputIo.MainConveyorAvailableToRear,
            rearAvailable);
    }

    private async Task ReceiveAtPlacementAsync(
        CancellationToken cancellationToken)
    {
        if (_transfer is not ConveyorTransfer.ReceivingBeforeEntry
            and not ConveyorTransfer.ReceivingAfterEntry)
        {
            _transfer = EntryCarrierDetected
                ? ConveyorTransfer.ReceivingAfterEntry
                : ConveyorTransfer.ReceivingBeforeEntry;
        }

        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
        try
        {
            await _placement.PrepareToReceiveAsync(cancellationToken);
            if (_transfer == ConveyorTransfer.ReceivingBeforeEntry)
            {
                _io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
            }

            if (!_placementWork.CarrierPresent)
            {
                try
                {
                    StartMotor(cancellationToken);
                    if (_transfer == ConveyorTransfer.ReceivingBeforeEntry)
                    {
                        await _io.WaitForInputAsync(
                            InputIo.MainConveyorEntryCarrierDetected,
                            true,
                            cancellationToken);
                        _transfer = ConveyorTransfer.ReceivingAfterEntry;
                    }

                    _io.SetOutput(
                        OutputIo.MainConveyorReadyToFront2,
                        false);
                    await _placement.WaitForCarrierAsync(cancellationToken);
                }
                finally
                {
                    StopMotor();
                }
            }
        }
        finally
        {
            _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        }

        _transfer = ConveyorTransfer.None;
    }

    private async Task MoveCarrierAsync(
        ConveyorTransfer transfer,
        ConveyorStation source,
        ConveyorStation destination,
        CancellationToken cancellationToken)
    {
        _transfer = transfer;
        ResetSmema();
        if (!destination.CarrierPresent)
        {
            await Task.WhenAll(
                source.ReleaseAsync(cancellationToken),
                destination.PrepareToReceiveAsync(cancellationToken));
            try
            {
                StartMotor(cancellationToken);
                await destination.WaitForCarrierAsync(cancellationToken);
            }
            finally
            {
                StopMotor();
            }
        }

        await Task.WhenAll(
            source.RaiseBackupPlateAsync(cancellationToken),
            destination.SeatAsync(cancellationToken));
        _transfer = ConveyorTransfer.None;
    }

    private async Task DischargeInspectionAsync(
        CancellationToken cancellationToken)
    {
        if (!InspectionDischargeActive)
        {
            _transfer = ExitCarrierDetected
                ? ConveyorTransfer.DischargingInspectionFromExit
                : ConveyorTransfer.DischargingInspectionToExit;
        }

        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetOutput(
            OutputIo.MainConveyorAvailableToRear,
            _transfer == ConveyorTransfer.DischargingInspectionToExit
            || ExitCarrierDetected);
        try
        {
            if (_inspectionWork.CarrierPresent)
            {
                await _inspection.ReleaseAsync(cancellationToken);
            }

            if (_transfer == ConveyorTransfer.DischargingInspectionToExit
                || ExitCarrierDetected)
            {
                try
                {
                    StartMotor(cancellationToken);
                    if (_transfer
                        == ConveyorTransfer.DischargingInspectionToExit)
                    {
                        await _io.WaitForInputAsync(
                            InputIo.MainConveyorExitCarrierDetected,
                            true,
                            cancellationToken);
                        _transfer =
                            ConveyorTransfer.DischargingInspectionFromExit;
                    }

                    await _io.WaitForInputAsync(
                        InputIo.MainConveyorExitCarrierDetected,
                        false,
                        cancellationToken);
                }
                finally
                {
                    StopMotor();
                }
            }
        }
        finally
        {
            _io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
        }

        await _inspection.RaiseBackupPlateAsync(cancellationToken);
        _transfer = ConveyorTransfer.None;
    }

    private void StartMotor(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorReverse, false);
        _io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorRun, true);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == InputIo.MainConveyorEntryCarrierDetected
            && value
            && _transfer == ConveyorTransfer.ReceivingBeforeEntry)
        {
            _transfer = ConveyorTransfer.ReceivingAfterEntry;
        }

        if (input == InputIo.MainConveyorExitCarrierDetected
            && value
            && _transfer == ConveyorTransfer.DischargingInspectionToExit)
        {
            _transfer = ConveyorTransfer.DischargingInspectionFromExit;
        }

        if (input is InputIo.MainConveyorAvailableFromFront2
            or InputIo.MainConveyorReadyFromRear
            or InputIo.MainConveyorEntryCarrierDetected
            or InputIo.MainConveyorExitCarrierDetected)
        {
            Changed?.Invoke();
        }
    }

    private void OnCarrierChanged(
        StationPosition station,
        bool value)
    {
        if (!value)
        {
            return;
        }

        if (station == StationPosition.PcbPlacement
            && (_transfer is ConveyorTransfer.ReceivingBeforeEntry
                or ConveyorTransfer.ReceivingAfterEntry))
        {
            _transfer = ConveyorTransfer.None;
        }

        switch (station)
        {
            case StationPosition.BoltFastening
                when _transfer
                    == ConveyorTransfer.PcbPlacementToBoltFastening:
                _placementWork.TransferAssembliesTo(
                    _boltFasteningWork);
                break;

            case StationPosition.Inspection
                when _transfer
                    == ConveyorTransfer.BoltFasteningToInspection:
                _boltFasteningWork.TransferAssembliesTo(
                    _inspectionWork);
                break;
        }
    }

    private void NotifyChanged() => Changed?.Invoke();

    private enum StationPosition
    {
        PcbPlacement,
        BoltFastening,
        Inspection,
    }

    private enum ConveyorTransfer
    {
        None,
        ReceivingBeforeEntry,
        ReceivingAfterEntry,
        PcbPlacementToBoltFastening,
        BoltFasteningToInspection,
        DischargingInspectionToExit,
        DischargingInspectionFromExit,
    }
}
