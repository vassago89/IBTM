using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed class MainConveyor
{
    private readonly IIoService _io;
    private readonly OperationCancellation _operations;
    private readonly StationWork _placementWork;
    private readonly StationWork _boltFasteningWork;
    private readonly StationWork _inspectionWork;
    private readonly bool _placementEnabled;
    private readonly bool _boltFasteningEnabled;
    private readonly bool _inspectionEnabled;
    private readonly bool _inspectionBypassToNg;
    private CancellationTokenSource? _automaticCancellation;
    private CancellationTokenSource? _manualCancellation;
    private CancellationTokenRegistration _manualStopRegistration;
    private volatile ConveyorTransfer _transfer;

    public MainConveyor(
        IIoService io,
        OperationCancellation operations,
        StationWork placementWork,
        StationWork boltFasteningWork,
        StationWork inspectionWork,
        bool placementEnabled,
        bool boltFasteningEnabled,
        bool inspectionEnabled,
        bool inspectionBypassToNg)
    {
        _io = io;
        _operations = operations;
        _placementWork = placementWork;
        _boltFasteningWork = boltFasteningWork;
        _inspectionWork = inspectionWork;
        _placementEnabled = placementEnabled;
        _boltFasteningEnabled = boltFasteningEnabled;
        _inspectionEnabled = inspectionEnabled;
        _inspectionBypassToNg = inspectionBypassToNg;
        io.InputChanged += OnInputChanged;
        placementWork.Changed += OnWorkChanged;
        boltFasteningWork.Changed += OnWorkChanged;
        inspectionWork.Changed += OnWorkChanged;
        placementWork.CarrierChanged += value =>
            OnCarrierChanged(ConveyorStation.PcbPlacement, value);
        boltFasteningWork.CarrierChanged += value =>
            OnCarrierChanged(ConveyorStation.BoltFastening, value);
        inspectionWork.CarrierChanged += value =>
            OnCarrierChanged(ConveyorStation.Inspection, value);
    }

    public event Action? Changed;
    public bool RunCommandOn => _io.GetOutput(OutputIo.MainConveyorRun);

    public MainConveyorState State
    {
        get
        {
            if (_io.GetInput(
                    InputIo.MainConveyorExitCarrierDetected))
            {
                return _io.GetInput(InputIo.MainConveyorReadyFromRear)
                    ? MainConveyorState.DischargingInspectionCarrier
                    : MainConveyorState.WaitingForRearEquipment;
            }

            if (_placementWork.CanReceive
                && _io.GetInput(
                    InputIo.MainConveyorEntryCarrierDetected))
            {
                return MainConveyorState.ReceivingFrontCarrier;
            }

            if (_transfer == ConveyorTransfer.DischargingInspectionFromExit)
            {
                return MainConveyorState.DischargingInspectionCarrier;
            }

            if (_transfer == ConveyorTransfer.DischargingInspectionToExit)
            {
                return _io.GetInput(InputIo.MainConveyorReadyFromRear)
                    ? MainConveyorState.DischargingInspectionCarrier
                    : MainConveyorState.WaitingForRearEquipment;
            }

            if (_transfer is ConveyorTransfer.ReceivingBeforeEntry
                or ConveyorTransfer.ReceivingAfterEntry)
            {
                return MainConveyorState.ReceivingFrontCarrier;
            }

            if (_transfer == ConveyorTransfer.BoltFasteningToInspection)
            {
                return MainConveyorState.MovingBoltFasteningToInspection;
            }

            if (_transfer == ConveyorTransfer.PcbPlacementToBoltFastening)
            {
                return MainConveyorState.MovingPcbPlacementToBoltFastening;
            }

            if (BoltFasteningToInspectionPrepared)
            {
                return MainConveyorState.BoltFasteningCarrierBetweenStations;
            }

            if (PlacementToBoltFasteningPrepared)
            {
                return MainConveyorState.PcbPlacementCarrierBetweenStations;
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

            if (_inspectionWork.CarrierPresent && !_inspectionWork.Ready)
            {
                return MainConveyorState.SeatingInspectionCarrier;
            }

            if (_boltFasteningWork.CarrierPresent
                && !_boltFasteningWork.Ready)
            {
                return MainConveyorState.SeatingBoltFasteningCarrier;
            }

            if (_placementWork.CarrierPresent && !_placementWork.Ready)
            {
                return MainConveyorState.SeatingPcbPlacementCarrier;
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
        var runToken = _manualCancellation.Token;
        runToken.ThrowIfCancellationRequested();
        _manualStopRegistration = runToken.Register(StopMotor);
        StartMotor();
        if (runToken.IsCancellationRequested)
        {
            StopMotor();
        }
        runToken.ThrowIfCancellationRequested();
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        Stop();
        using var runCancellation = _operations.Link(cancellationToken);
        _automaticCancellation = runCancellation;
        cancellationToken = runCancellation.Token;
        using var stopRegistration = cancellationToken.Register(StopMotor);
        using var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        Changed += OnStateChanged;
        try
        {
            BypassDisabledWork(ConveyorStation.PcbPlacement);
            BypassDisabledWork(ConveyorStation.BoltFastening);
            BypassDisabledWork(ConveyorStation.Inspection);
            while (!cancellationToken.IsCancellationRequested)
            {
                switch (State)
                {
                    case MainConveyorState.SeatingInspectionCarrier:
                        await SeatCarrierAsync(
                            OutputIo.InspectionStopperUp,
                            OutputIo.InspectionBackupPlateUp,
                            cancellationToken);
                        break;

                    case MainConveyorState.SeatingBoltFasteningCarrier:
                        await SeatCarrierAsync(
                            OutputIo.BoltFasteningStopperUp,
                            OutputIo.BoltFasteningBackupPlateUp,
                            cancellationToken);
                        break;

                    case MainConveyorState.SeatingPcbPlacementCarrier:
                        await SeatCarrierAsync(
                            OutputIo.PcbPlacementStopperUp,
                            OutputIo.PcbPlacementBackupPlateUp,
                            cancellationToken);
                        break;

                    case MainConveyorState.DischargingInspectionCarrier:
                        await DischargeInspectionAsync(cancellationToken);
                        break;

                    case MainConveyorState.MovingBoltFasteningToInspection:
                        await MoveCarrierAsync(
                            ConveyorTransfer.BoltFasteningToInspection,
                            OutputIo.BoltFasteningStopperUp,
                            OutputIo.BoltFasteningBackupPlateUp,
                            InputIo.InspectionCarrierPresent,
                            OutputIo.InspectionStopperUp,
                            OutputIo.InspectionBackupPlateUp,
                            cancellationToken);
                        break;

                    case MainConveyorState.MovingPcbPlacementToBoltFastening:
                        await MoveCarrierAsync(
                            ConveyorTransfer.PcbPlacementToBoltFastening,
                            OutputIo.PcbPlacementStopperUp,
                            OutputIo.PcbPlacementBackupPlateUp,
                            InputIo.BoltFasteningCarrierPresent,
                            OutputIo.BoltFasteningStopperUp,
                            OutputIo.BoltFasteningBackupPlateUp,
                            cancellationToken);
                        break;

                    case MainConveyorState.ReceivingFrontCarrier:
                        await ReceiveAtPlacementAsync(cancellationToken);
                        break;

                    default:
                        UpdateSmema();
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
            ResetSmema();
            StopMotor();
            if (ReferenceEquals(_automaticCancellation, runCancellation))
            {
                _automaticCancellation = null;
            }
        }
    }

    public void Stop()
    {
        _automaticCancellation?.Cancel();
        _manualCancellation?.Cancel();
        _manualStopRegistration.Dispose();
        _manualCancellation?.Dispose();
        _manualCancellation = null;
        StopMotor();
        ResetSmema();
    }

    private void ResetSmema()
    {
        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
    }

    private void StopMotor() =>
        _io.SetOutput(OutputIo.MainConveyorRun, false);

    private bool CanOfferToRear =>
        !_inspectionBypassToNg
        && (_io.GetInput(InputIo.MainConveyorExitCarrierDetected)
            || InspectionDischargeActive
            || !_inspectionWork.HasNg
            && _inspectionWork.CanTransfer);

    private bool CanDischargeInspection =>
        CanOfferToRear
        && _io.GetInput(InputIo.MainConveyorReadyFromRear);

    private bool CanMoveBoltFasteningToInspection =>
        _boltFasteningWork.CanTransfer
        && _inspectionWork.CanReceive;

    private bool CanMovePlacementToBoltFastening =>
        _placementWork.CanTransfer
        && _boltFasteningWork.CanReceive;

    private bool PlacementToBoltFasteningPrepared =>
        TransferPrepared(
            InputIo.PcbPlacementCarrierPresent,
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperDown,
            InputIo.BoltFasteningCarrierPresent,
            InputIo.BoltFasteningBackupPlateDown,
            InputIo.BoltFasteningStopperUp);

    private bool BoltFasteningToInspectionPrepared =>
        TransferPrepared(
            InputIo.BoltFasteningCarrierPresent,
            InputIo.BoltFasteningBackupPlateDown,
            InputIo.BoltFasteningStopperDown,
            InputIo.InspectionCarrierPresent,
            InputIo.InspectionBackupPlateDown,
            InputIo.InspectionStopperUp);

    private bool CanReceiveAtPlacement =>
        _placementWork.CanReceive
        && (_io.GetInput(InputIo.MainConveyorAvailableFromFront2)
            || _io.GetInput(InputIo.MainConveyorEntryCarrierDetected));

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
            _transfer = _io.GetInput(
                InputIo.MainConveyorEntryCarrierDetected)
                ? ConveyorTransfer.ReceivingAfterEntry
                : ConveyorTransfer.ReceivingBeforeEntry;
        }

        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
        try
        {
            await PrepareDestinationAsync(
                OutputIo.PcbPlacementStopperUp,
                OutputIo.PcbPlacementBackupPlateUp,
                cancellationToken);
            if (_transfer == ConveyorTransfer.ReceivingBeforeEntry)
            {
                _io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
            }

            if (!_placementWork.CarrierPresent)
            {
                StartMotor();
                try
                {
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
                    await _io.WaitForInputAsync(
                        InputIo.PcbPlacementCarrierPresent,
                        true,
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
            _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        }

        _transfer = ConveyorTransfer.None;
        await SeatCarrierAsync(
            OutputIo.PcbPlacementStopperUp,
            OutputIo.PcbPlacementBackupPlateUp,
            cancellationToken);
    }

    private async Task MoveCarrierAsync(
        ConveyorTransfer transfer,
        OutputIo sourceStopper,
        OutputIo sourceBackupPlate,
        InputIo destinationCarrier,
        OutputIo destinationStopper,
        OutputIo destinationBackupPlate,
        CancellationToken cancellationToken)
    {
        _transfer = transfer;
        ResetSmema();
        await Task.WhenAll(
            ReleaseSourceAsync(
                sourceStopper,
                sourceBackupPlate,
                cancellationToken),
            PrepareDestinationAsync(
                destinationStopper,
                destinationBackupPlate,
                cancellationToken));

        if (!_io.GetInput(destinationCarrier))
        {
            StartMotor();
            try
            {
                await _io.WaitForInputAsync(
                    destinationCarrier,
                    true,
                    cancellationToken);
            }
            finally
            {
                StopMotor();
            }
        }

        await Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                sourceBackupPlate,
                true,
                cancellationToken),
            SeatCarrierAsync(
                destinationStopper,
                destinationBackupPlate,
                cancellationToken));
        _transfer = ConveyorTransfer.None;
    }

    private async Task DischargeInspectionAsync(
        CancellationToken cancellationToken)
    {
        if (!InspectionDischargeActive)
        {
            _transfer = _io.GetInput(
                InputIo.MainConveyorExitCarrierDetected)
                ? ConveyorTransfer.DischargingInspectionFromExit
                : ConveyorTransfer.DischargingInspectionToExit;
        }

        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetOutput(
            OutputIo.MainConveyorAvailableToRear,
            _transfer == ConveyorTransfer.DischargingInspectionToExit
            || _io.GetInput(InputIo.MainConveyorExitCarrierDetected));
        try
        {
            if (_inspectionWork.CarrierPresent)
            {
                await ReleaseSourceAsync(
                    OutputIo.InspectionStopperUp,
                    OutputIo.InspectionBackupPlateUp,
                    cancellationToken);
            }

            if (_transfer == ConveyorTransfer.DischargingInspectionToExit
                || _io.GetInput(
                    InputIo.MainConveyorExitCarrierDetected))
            {
                StartMotor();
                try
                {
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

        await _io.SetOutputAndWaitAsync(
            OutputIo.InspectionBackupPlateUp,
            true,
            cancellationToken);
        _transfer = ConveyorTransfer.None;
    }

    private Task ReleaseSourceAsync(
        OutputIo stopper,
        OutputIo backupPlate,
        CancellationToken cancellationToken) =>
        Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                stopper,
                false,
                cancellationToken),
            _io.SetOutputAndWaitAsync(
                backupPlate,
                false,
                cancellationToken));

    private Task PrepareDestinationAsync(
        OutputIo stopper,
        OutputIo backupPlate,
        CancellationToken cancellationToken) =>
        Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                stopper,
                true,
                cancellationToken),
            _io.SetOutputAndWaitAsync(
                backupPlate,
                false,
                cancellationToken));

    private async Task SeatCarrierAsync(
        OutputIo stopper,
        OutputIo backupPlate,
        CancellationToken cancellationToken)
    {
        await _io.SetOutputAndWaitAsync(
            stopper,
            true,
            cancellationToken);
        await _io.SetOutputAndWaitAsync(
            backupPlate,
            true,
            cancellationToken);
        await _io.SetOutputAndWaitAsync(
            stopper,
            false,
            cancellationToken);
    }

    private void StartMotor()
    {
        _io.SetOutput(OutputIo.MainConveyorReverse, false);
        _io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
        _io.SetOutput(OutputIo.MainConveyorRun, true);
    }

    private bool TransferPrepared(
        InputIo sourceCarrier,
        InputIo sourceBackupPlateDown,
        InputIo sourceStopperDown,
        InputIo destinationCarrier,
        InputIo destinationBackupPlateDown,
        InputIo destinationStopperUp) =>
        !_io.GetInput(sourceCarrier)
        && _io.GetInput(sourceBackupPlateDown)
        && _io.GetInput(sourceStopperDown)
        && !_io.GetInput(destinationCarrier)
        && _io.GetInput(destinationBackupPlateDown)
        && _io.GetInput(destinationStopperUp);

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
        ConveyorStation station,
        bool value)
    {
        if (!value)
        {
            return;
        }

        if (station == ConveyorStation.PcbPlacement
            && (_transfer is ConveyorTransfer.ReceivingBeforeEntry
                or ConveyorTransfer.ReceivingAfterEntry))
        {
            _transfer = ConveyorTransfer.None;
        }

        switch (station)
        {
            case ConveyorStation.BoltFastening:
                _boltFasteningWork.SetAssemblies(
                    _placementWork.Assemblies);
                break;

            case ConveyorStation.Inspection:
                _inspectionWork.SetAssemblies(
                    _boltFasteningWork.Assemblies);
                break;
        }

        BypassDisabledWork(station);
    }

    private void BypassDisabledWork(ConveyorStation station)
    {
        var (enabled, work) = station switch
        {
            ConveyorStation.PcbPlacement =>
                (_placementEnabled, (StationWork)_placementWork),
            ConveyorStation.BoltFastening =>
                (_boltFasteningEnabled, _boltFasteningWork),
            ConveyorStation.Inspection =>
                (_inspectionEnabled, _inspectionWork),
            _ => throw new ArgumentOutOfRangeException(nameof(station)),
        };
        if (!enabled && work.CarrierPresent)
        {
            work.Complete();
        }
    }

    private void OnWorkChanged() => Changed?.Invoke();

    private enum ConveyorStation
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
