using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
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
    private volatile ConveyorTransfer _transfer;
    private bool _repeat;
    private bool _returningFromEntry;

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
        _placement.CarrierChanged += value => OnCarrierChanged(StationPosition.PcbPlacement, value);
        _boltFastening.CarrierChanged += value => OnCarrierChanged(StationPosition.BoltFastening, value);
        _inspection.CarrierChanged += value => OnCarrierChanged(StationPosition.Inspection, value);
    }

    public override event Action? Changed;
    public bool RunCommandOn
    {
        get
        {
            return _io.GetOutput(OutputIo.MainConveyorRun);
        }
    }

    public bool EntryCarrierDetected
    {
        get
        {
            return _io.GetInput(InputIo.MainConveyorEntryCarrierDetected);
        }
    }

    public bool ExitCarrierDetected
    {
        get
        {
            return _io.GetInput(InputIo.MainConveyorExitCarrierDetected);
        }
    }

    public MainConveyorState State
    {
        get
        {
            // The destination is transfer history, not proof of a carrier's location.
            // After a stop between sensors, wait for presence feedback before resuming.
            if (!RunCommandOn
                && (_transfer switch
                {
                    ConveyorTransfer.ReceivingAfterEntry =>
                        !EntryCarrierDetected && !_placementWork.CarrierPresent,
                    ConveyorTransfer.PcbPlacementToBoltFastening =>
                        !_placementWork.CarrierPresent && !_boltFasteningWork.CarrierPresent,
                    ConveyorTransfer.BoltFasteningToInspection =>
                        !_boltFasteningWork.CarrierPresent && !_inspectionWork.CarrierPresent,
                    ConveyorTransfer.DischargingInspectionToExit =>
                        !_inspectionWork.CarrierPresent && !ExitCarrierDetected,
                    _ => false,
                }))
            {
                return MainConveyorState.CarrierPositionUnknown;
            }

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

            if (_inspectionWork.CarrierPresent && !_inspectionWork.CarrierSeated)
            {
                return MainConveyorState.SeatingInspectionCarrier;
            }

            if (_boltFasteningWork.CarrierPresent && !_boltFasteningWork.CarrierSeated)
            {
                return MainConveyorState.SeatingBoltFasteningCarrier;
            }

            if (_placementWork.CarrierPresent && !_placementWork.CarrierSeated)
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

    public async Task RunMotorAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        using var runCancellation = _operations.Link(cancellationToken);
        _manualCancellation = runCancellation;
        cancellationToken = runCancellation.Token;
        try
        {
            using var stopRegistration = cancellationToken.Register(StopMotor);
            StartMotor(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_manualCancellation, runCancellation))
                _manualCancellation = null;
            StopMotor();
            ResetSmema();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        _repeat = repeat;
        try
        {
            await RunControlledAsync(token => RunLoopAsync(ExecuteAsync, token), cancellationToken);
        }
        finally
        {
            _repeat = false;
        }
    }

    public async Task ReturnToStartAsync(CancellationToken cancellationToken)
    {
        var carriers = (EntryCarrierDetected ? 1 : 0)
            + (_placement.CarrierPresent ? 1 : 0)
            + (_boltFastening.CarrierPresent ? 1 : 0)
            + (_inspection.CarrierPresent ? 1 : 0);
        if (carriers > 1 || ExitCarrierDetected)
            throw new InvalidOperationException("Main conveyor return requires one carrier and a clear exit.");

        // Reverse travel does not transfer production results to stations it passes.
        _transfer = ConveyorTransfer.None;
        _repeat = true;
        try
        {
            await RunControlledAsync(ReturnCarrierAsync, cancellationToken);
        }
        finally
        {
            _repeat = false;
        }
    }

    private async Task ReturnCarrierAsync(CancellationToken cancellationToken)
    {
        if (!_returningFromEntry)
        {
            if (!EntryCarrierDetected
                && !_placement.CarrierPresent
                && !_boltFastening.CarrierPresent
                && !_inspection.CarrierPresent)
            {
                throw new InvalidOperationException("Return carrier position is unknown. Restore carrier presence before restarting.");
            }

            await Task.WhenAll(
                _placement.ReleaseAsync(cancellationToken),
                _boltFastening.ReleaseAsync(cancellationToken),
                _inspection.ReleaseAsync(cancellationToken));
            await RunUntilAsync(InputIo.MainConveyorEntryCarrierDetected, true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _returningFromEntry = true;
        }

        if (!_placement.CarrierPresent)
        {
            if (!EntryCarrierDetected)
                throw new InvalidOperationException("Return carrier is not at the entry or Station 1.");
            await ReceiveAtPlacementAsync(cancellationToken);
        }

        await _placement.SeatAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _returningFromEntry = false;
    }

    internal async Task RunControlledAsync(
        Func<CancellationToken, Task> run,
        CancellationToken cancellationToken)
    {
        Stop();
        using var runCancellation = _operations.Link(cancellationToken);
        _automaticCancellation = runCancellation;
        cancellationToken = runCancellation.Token;
        using var stopRegistration = cancellationToken.Register(StopMotor);
        List<Exception>? failures = null;
        try
        {
            await run(cancellationToken);
        }
        catch (Exception exception)
        {
            failures = [exception];
        }
        finally
        {
            try
            {
                StopMotor();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                ResetSmema();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (ReferenceEquals(_automaticCancellation, runCancellation))
            {
                _automaticCancellation = null;
            }
        }

        if (failures?.Count == 1)
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures is not null)
        {
            throw new AggregateException("Conveyor operation or cleanup failed.", failures);
        }
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        switch (State)
        {
            case MainConveyorState.CarrierPositionUnknown:
                ResetSmema();
                return WaitForChangeAsync(cancellationToken);
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
                    _boltFastening,
                    _inspection,
                    cancellationToken);
            case MainConveyorState.MovingPcbPlacementToBoltFastening:
                return MoveCarrierAsync(
                    ConveyorTransfer.PcbPlacementToBoltFastening,
                    _placement,
                    _boltFastening,
                    cancellationToken);
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
            StopMotor();
            ResetSmema();
        }
    }

    private void ResetSmema()
    {
        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
    }

    private void StopMotor()
    {
        _io.SetOutput(OutputIo.MainConveyorRun, false);
    }

    private bool CanOfferToRear
    {
        get
        {
            return ExitCarrierDetected
                || InspectionDischargeActive
                || !_repeat && !_routeInspectionToNg()
                && _inspectionWork.CanTransfer;
        }
    }

    private bool CanDischargeInspection
    {
        get
        {
            return CanOfferToRear && _io.GetInput(InputIo.MainConveyorReadyFromRear);
        }
    }

    private bool CanMoveBoltFasteningToInspection
    {
        get
        {
            return _boltFasteningWork.CanTransfer && _inspectionWork.CanReceive;
        }
    }

    private bool CanMovePlacementToBoltFastening
    {
        get
        {
            return _placementWork.CanTransfer && _boltFasteningWork.CanReceive;
        }
    }

    private bool CanReceiveAtPlacement
    {
        get
        {
            return _placementWork.CanReceive
                && (!_repeat && _io.GetInput(InputIo.MainConveyorAvailableFromFront2)
                    || EntryCarrierDetected);
        }
    }

    private bool InspectionDischargeActive
    {
        get
        {
            return _transfer is ConveyorTransfer.DischargingInspectionToExit
                or ConveyorTransfer.DischargingInspectionFromExit;
        }
    }

    private void UpdateSmema()
    {
        var rearAvailable = CanOfferToRear;
        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, !_repeat && _placementWork.CanReceive && !rearAvailable);
        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, rearAvailable);
    }

    private async Task ReceiveAtPlacementAsync(CancellationToken cancellationToken)
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
            if (!_repeat && _transfer == ConveyorTransfer.ReceivingBeforeEntry)
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

                    _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
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

    private async Task DischargeInspectionAsync(CancellationToken cancellationToken)
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
                    if (_transfer == ConveyorTransfer.DischargingInspectionToExit)
                    {
                        await _io.WaitForInputAsync(
                            InputIo.MainConveyorExitCarrierDetected,
                            true,
                            cancellationToken);
                        _transfer = ConveyorTransfer.DischargingInspectionFromExit;
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

    internal async Task RunUntilAsync(
        InputIo destination,
        bool reverse,
        CancellationToken cancellationToken)
    {
        if (_io.GetInput(destination))
            return;
        try
        {
            StartMotor(cancellationToken, reverse);
            await _io.WaitForInputAsync(destination, true, cancellationToken);
        }
        finally
        {
            StopMotor();
        }
    }

    private void StartMotor(CancellationToken cancellationToken, bool reverse = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorReverse, reverse);
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

    private void OnCarrierChanged(StationPosition station, bool value)
    {
        if (!value)
        {
            return;
        }

        if (station == StationPosition.PcbPlacement
            && (_transfer is ConveyorTransfer.ReceivingBeforeEntry or ConveyorTransfer.ReceivingAfterEntry))
        {
            _transfer = ConveyorTransfer.None;
        }

        switch (station)
        {
            case StationPosition.BoltFastening when _transfer == ConveyorTransfer.PcbPlacementToBoltFastening:
                _placementWork.TransferAssembliesTo(_boltFasteningWork);
                break;

            case StationPosition.Inspection when _transfer == ConveyorTransfer.BoltFasteningToInspection:
                _boltFasteningWork.TransferAssembliesTo(_inspectionWork);
                break;
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

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
