using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;

namespace IBTM.Conveyor;

public sealed class MainConveyor
{
    private readonly IIoService _io;
    private readonly OperationCancellation _operations;
    private readonly PcbPlacementWork _placementWork;
    private readonly BoltFasteningWork _boltFasteningWork;
    private readonly InspectionWork _inspectionWork;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenRegistration _stopRegistration;

    public MainConveyor(
        IIoService io,
        OperationCancellation operations,
        PcbPlacementWork placementWork,
        BoltFasteningWork boltFasteningWork,
        InspectionWork inspectionWork)
    {
        _io = io;
        _operations = operations;
        _placementWork = placementWork;
        _boltFasteningWork = boltFasteningWork;
        _inspectionWork = inspectionWork;
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
    public event Action<ConveyorStation, bool>? CarrierChanged;

    public bool RunCommandOn => _io.GetOutput(OutputIo.MainConveyorRun);

    private ConveyorState State
    {
        get
        {
            if (CanDischargeInspection)
            {
                return ConveyorState.DischargingInspectionCarrier;
            }

            if (CanMoveBoltFasteningToInspection)
            {
                return ConveyorState.MovingBoltFasteningToInspection;
            }

            if (CanMovePlacementToBoltFastening)
            {
                return ConveyorState.MovingPcbPlacementToBoltFastening;
            }

            if (_inspectionWork.CarrierPresent && !_inspectionWork.Ready)
            {
                return ConveyorState.SeatingInspectionCarrier;
            }

            if (_boltFasteningWork.CarrierPresent
                && !_boltFasteningWork.Ready)
            {
                return ConveyorState.SeatingBoltFasteningCarrier;
            }

            if (_placementWork.CarrierPresent && !_placementWork.Ready)
            {
                return ConveyorState.SeatingPcbPlacementCarrier;
            }

            if (CanReceiveAtPlacement)
            {
                return ConveyorState.ReceivingFrontCarrier;
            }

            if (CanOfferToRear)
            {
                return ConveyorState.WaitingForRearEquipment;
            }

            return _placementWork.CarrierPresent
                ? ConveyorState.Idle
                : ConveyorState.WaitingForFrontCarrier;
        }
    }

    public bool HasCarrier(ConveyorStation station) =>
        Work(station).CarrierPresent;

    public void BypassWork(ConveyorStation station) =>
        Work(station).Complete();

    public void RunMotor(CancellationToken cancellationToken = default)
    {
        Stop();
        _runCancellation = _operations.Link(cancellationToken);
        var runToken = _runCancellation.Token;
        runToken.ThrowIfCancellationRequested();
        _stopRegistration = runToken.Register(StopMotor);
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
        _runCancellation = _operations.Link(cancellationToken);
        cancellationToken = _runCancellation.Token;
        _stopRegistration = cancellationToken.Register(StopMotor);
        using var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                switch (State)
                {
                    case ConveyorState.SeatingInspectionCarrier:
                        await SeatCarrierAsync(
                            OutputIo.InspectionStopperUp,
                            OutputIo.InspectionBackupPlateUp,
                            cancellationToken);
                        break;

                    case ConveyorState.SeatingBoltFasteningCarrier:
                        await SeatCarrierAsync(
                            OutputIo.BoltFasteningStopperUp,
                            OutputIo.BoltFasteningBackupPlateUp,
                            cancellationToken);
                        break;

                    case ConveyorState.SeatingPcbPlacementCarrier:
                        await SeatCarrierAsync(
                            OutputIo.PcbPlacementStopperUp,
                            OutputIo.PcbPlacementBackupPlateUp,
                            cancellationToken);
                        break;

                    case ConveyorState.DischargingInspectionCarrier:
                        await DischargeInspectionAsync(cancellationToken);
                        break;

                    case ConveyorState.MovingBoltFasteningToInspection:
                        await MoveCarrierAsync(
                            _boltFasteningWork,
                            _inspectionWork,
                            OutputIo.BoltFasteningStopperUp,
                            OutputIo.BoltFasteningBackupPlateUp,
                            InputIo.InspectionCarrierJigPresent,
                            OutputIo.InspectionStopperUp,
                            OutputIo.InspectionBackupPlateUp,
                            cancellationToken);
                        break;

                    case ConveyorState.MovingPcbPlacementToBoltFastening:
                        await MoveCarrierAsync(
                            _placementWork,
                            _boltFasteningWork,
                            OutputIo.PcbPlacementStopperUp,
                            OutputIo.PcbPlacementBackupPlateUp,
                            InputIo.BoltFasteningCarrierJigPresent,
                            OutputIo.BoltFasteningStopperUp,
                            OutputIo.BoltFasteningBackupPlateUp,
                            cancellationToken);
                        break;

                    case ConveyorState.ReceivingFrontCarrier:
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
            _stopRegistration.Dispose();
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
    }

    public void Stop()
    {
        _runCancellation?.Cancel();
        _stopRegistration.Dispose();
        _runCancellation?.Dispose();
        _runCancellation = null;
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
        _inspectionWork.CanTransfer
        && !_inspectionWork.HasNg;

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
        && _io.GetInput(InputIo.MainConveyorAvailableFromFront2);

    private void UpdateSmema()
    {
        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetOutput(
            OutputIo.MainConveyorAvailableToRear,
            CanOfferToRear);
    }

    private async Task ReceiveAtPlacementAsync(
        CancellationToken cancellationToken)
    {
        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, false);
        try
        {
            await PrepareDestinationAsync(
                OutputIo.PcbPlacementStopperUp,
                OutputIo.PcbPlacementBackupPlateUp,
                cancellationToken);
            _io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
            StartMotor();
            try
            {
                await _io.WaitForInputAsync(
                    InputIo.PcbPlacementCarrierJigPresent,
                    true,
                    cancellationToken);
            }
            finally
            {
                StopMotor();
            }
        }
        finally
        {
            _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        }

        await SeatCarrierAsync(
            OutputIo.PcbPlacementStopperUp,
            OutputIo.PcbPlacementBackupPlateUp,
            cancellationToken);
    }

    private async Task MoveCarrierAsync(
        StationWork sourceWork,
        StationWork destinationWork,
        OutputIo sourceStopper,
        OutputIo sourceBackupPlate,
        InputIo destinationCarrier,
        OutputIo destinationStopper,
        OutputIo destinationBackupPlate,
        CancellationToken cancellationToken)
    {
        var assemblies = sourceWork.Assemblies.ToArray();
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

        destinationWork.SetAssemblies(assemblies);

        await Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                sourceBackupPlate,
                true,
                cancellationToken),
            SeatCarrierAsync(
                destinationStopper,
                destinationBackupPlate,
                cancellationToken));
    }

    private async Task DischargeInspectionAsync(
        CancellationToken cancellationToken)
    {
        _io.SetOutput(OutputIo.MainConveyorReadyToFront2, false);
        _io.SetOutput(OutputIo.MainConveyorAvailableToRear, true);
        try
        {
            await ReleaseSourceAsync(
                OutputIo.InspectionStopperUp,
                OutputIo.InspectionBackupPlateUp,
                cancellationToken);
            StartMotor();
            try
            {
                await _io.WaitForInputAsync(
                    InputIo.InspectionCarrierJigPresent,
                    false,
                    cancellationToken);
            }
            finally
            {
                StopMotor();
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

    private StationWork Work(ConveyorStation station) => station switch
    {
        ConveyorStation.PcbPlacement => _placementWork,
        ConveyorStation.BoltFastening => _boltFasteningWork,
        ConveyorStation.Inspection => _inspectionWork,
        _ => throw new ArgumentOutOfRangeException(nameof(station)),
    };

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.MainConveyorAvailableFromFront2
            or InputIo.MainConveyorReadyFromRear)
        {
            Changed?.Invoke();
        }
    }

    private void OnCarrierChanged(
        ConveyorStation station,
        bool value) => CarrierChanged?.Invoke(station, value);

    private void OnWorkChanged() => Changed?.Invoke();

    private enum ConveyorState
    {
        Idle,
        WaitingForFrontCarrier,
        WaitingForRearEquipment,
        SeatingPcbPlacementCarrier,
        SeatingBoltFasteningCarrier,
        SeatingInspectionCarrier,
        ReceivingFrontCarrier,
        MovingPcbPlacementToBoltFastening,
        MovingBoltFasteningToInspection,
        DischargingInspectionCarrier,
    }
}
