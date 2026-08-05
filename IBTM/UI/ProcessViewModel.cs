using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.Stations.Inspection;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class ProcessViewModel : ObservableObject
{
    private readonly EquipmentState _state;
    private readonly EquipmentService _equipment;
    private readonly PcbBufferService _pcbBuffer;
    private readonly InspectionStation _inspection;
    private readonly IIoService _io;
    private readonly MachineSettings _settings;
    private readonly MotionService _pcbSupplyMotion;
    private readonly MotionService _pcbPlacementMotion;
    private readonly MotionService _boltFasteningMotion;
    private readonly MotionService _ngTransferMotion;

    [ObservableProperty] private MachineDisplayState _machineDisplayState;
    [ObservableProperty] private HandlerDisplayState _supplyDisplayState;
    [ObservableProperty] private HandlerDisplayState _placementDisplayState;
    [ObservableProperty] private StationDisplayState _boltDisplayState;
    [ObservableProperty] private StationDisplayState _inspectionDisplayState;
    [ObservableProperty] private NgHandlingDisplayState _ngHandlingDisplayState;
    [ObservableProperty] private OperatorMessage _operatorMessage;
    [ObservableProperty] private DisplayTone _machineTone;
    [ObservableProperty] private DisplayTone _supplyTone;
    [ObservableProperty] private DisplayTone _placementTone;
    [ObservableProperty] private DisplayTone _boltTone;
    [ObservableProperty] private DisplayTone _inspectionTone;
    [ObservableProperty] private DisplayTone _ngHandlingTone;
    [ObservableProperty] private string _pcbSupplyPosition = "X 0.000   Z 0.000";
    [ObservableProperty] private string _pcbPlacementPosition = "X 0.000   Y 0.000   Z 0.000";
    [ObservableProperty] private string _boltFasteningPosition = "X 0.000   Y 0.000   Z 0.000";
    [ObservableProperty] private string _ngTransferPosition = "X 0.000   Y 0.000   Z 0.000";

    [ObservableProperty] private double _pcbSupplyMapLeft = 220;
    [ObservableProperty] private double _pcbPlacementMapLeft = 429;
    [ObservableProperty] private double _pcbPlacementMapTop = 118;
    [ObservableProperty] private double _boltFasteningMapLeft = 200;
    [ObservableProperty] private double _boltFasteningMapTop = 210;
    [ObservableProperty] private double _ngTransferMapLeft = 122;
    [ObservableProperty] private double _ngTransferMapTop = 180;
    [ObservableProperty] private bool _pcbSupplyPcbDetected;
    [ObservableProperty] private bool _pcbPlacementPcbDetected;
    [ObservableProperty] private bool _pcbSupplyGripperClosed;
    [ObservableProperty] private bool _pcbPlacementGripperClosed;
    [ObservableProperty] private bool _pcbPlacementStopperUp;
    [ObservableProperty] private bool _pcbPlacementBackupPlateUp;
    [ObservableProperty] private bool _pcbPlacementLaserOutput;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PcbSupplyRotationAngle))]
    private bool _pcbSupplyRotated;
    [ObservableProperty] private bool _pcbSupplyUpstreamBoardAvailable;
    [ObservableProperty] private bool _pcbBufferPcbPresent;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PcbPlacementOccupied))]
    private bool _pcbPlacementHousing1Present;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PcbPlacementOccupied))]
    private bool _pcbPlacementHousing2Present;
    [ObservableProperty] private bool _boltFasteningCarrierJigPresent;
    [ObservableProperty] private bool _boltFasteningHousing1Present;
    [ObservableProperty] private bool _boltFasteningHousing2Present;
    [ObservableProperty] private bool _boltFasteningStopperUp;
    [ObservableProperty] private bool _boltFasteningBackupPlateUp;
    [ObservableProperty] private bool _inspectionCarrierJigPresent;
    [ObservableProperty] private bool _inspectionHousing1Present;
    [ObservableProperty] private bool _inspectionHousing2Present;
    [ObservableProperty] private bool _inspectionStopperUp;
    [ObservableProperty] private bool _inspectionBackupPlateUp;
    [ObservableProperty] private bool _inspectionGripperClosed;
    [ObservableProperty] private bool _inspectionLaserOutput;

    public ProcessViewModel(
        EquipmentState state,
        MachineSettings settings,
        EquipmentService equipment,
        PcbBufferService pcbBuffer,
        InspectionStation inspection,
        IIoService io,
        [FromKeyedServices(MotionGroup.PcbSupply)] MotionService pcbSupplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacement)] MotionService pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] MotionService boltFasteningMotion,
        [FromKeyedServices(MotionGroup.Inspection)] MotionService ngTransferMotion)
    {
        _state = state;
        _equipment = equipment;
        _pcbBuffer = pcbBuffer;
        _inspection = inspection;
        _io = io;
        _settings = settings;
        _pcbSupplyMotion = pcbSupplyMotion;
        _pcbPlacementMotion = pcbPlacementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _ngTransferMotion = ngTransferMotion;
        pcbSupplyMotion.PositionChanged += OnPcbSupplyPositionChanged;
        pcbPlacementMotion.PositionChanged += OnPcbPlacementPositionChanged;
        boltFasteningMotion.PositionChanged += OnBoltFasteningPositionChanged;
        ngTransferMotion.PositionChanged += OnNgTransferPositionChanged;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
        equipment.Stopping += CancelOperations;
        state.Changed += OnEquipmentStateChanged;
        state.Refresh();
        UpdateDisplayStates();
    }

    public bool IsError => _state.IsError;
    public bool IsHoming => _state.IsHoming;
    public bool IsVirtual =>
        _settings.ControlDriver == ControlDriver.Virtual;
    public bool ConveyorRunning => _state.ConveyorRunning;
    public BufferOwner BufferOwner => _state.BufferOwner;
    public bool BufferRecoveryRequired => _state.BufferRecoveryRequired;
    public bool PcbSupplyMoving => _state.PcbSupplyMoving;
    public bool PcbPlacementMoving => _state.PcbPlacementMoving;
    public bool BoltFasteningMoving => _state.BoltFasteningMoving;
    public bool NgTransferMoving => _state.InspectionMoving;
    public bool PcbSupplyActive => _state.PcbSupplyActive;
    public bool PcbPlacementActive => _state.PcbPlacementActive;
    public bool BufferConflict => _state.BufferConflict;
    public int NgCarrierCount => _state.NgCarrierCount;
    public int NgCarrierCapacity => _state.NgCarrierCapacity;
    public bool NgCarrierFull => _state.NgCarrierFull;
    public bool PcbPlacementOccupied =>
        _state.PlacementOccupied;
    public double PcbSupplyRotationAngle => PcbSupplyRotated ? 90 : 0;
    public bool EmergencyStopReleased => _state.EmergencyStopReleased;
    public bool DoorClosed => _state.DoorClosed;
    public bool AirPressureOk => _state.AirPressureOk;

    [RelayCommand]
    private void Stop() => _equipment.Stop();

    [RelayCommand]
    private void EStop() => _equipment.EmergencyStop();

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => _equipment.Reset();

    [RelayCommand(CanExecute = nameof(CanHome))]
    private Task HomeAsync(CancellationToken cancellationToken) =>
        _equipment.HomeAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStopHome))]
    private void StopHome() => HomeCommand.Cancel();

    [RelayCommand(CanExecute = nameof(CanRecoverBuffer))]
    private Task RecoverBufferAsync(CancellationToken cancellationToken) =>
        _pcbBuffer.RecoverAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanResetNgCarrierCount))]
    private void ResetNgCarrierCount() => _inspection.ResetNgCarrierCount();

    [RelayCommand(CanExecute = nameof(CanSupplyToBuffer))]
    private Task SupplyToBufferAsync() => _pcbBuffer.SupplyToBufferAsync();

    [RelayCommand(CanExecute = nameof(CanBufferToPlacement))]
    private Task BufferToPlacementAsync() =>
        _pcbBuffer.BufferToPlacementAsync();

    private bool CanReset() => _equipment.CanReset;
    private bool CanHome() => _equipment.CanHome;
    private bool CanStopHome() => _state.IsHoming;
    private bool CanRecoverBuffer() => _pcbBuffer.CanRecover;
    private bool CanResetNgCarrierCount() => _state.NgCarrierFull;
    private bool CanSupplyToBuffer() => _pcbBuffer.CanSupplyToBuffer;
    private bool CanBufferToPlacement() => _pcbBuffer.CanBufferToPlacement;

    private void CancelOperations()
    {
        HomeCommand.Cancel();
        RecoverBufferCommand.Cancel();
    }

    private void NotifyCanExecuteChanged()
    {
        ResetCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        StopHomeCommand.NotifyCanExecuteChanged();
        RecoverBufferCommand.NotifyCanExecuteChanged();
        ResetNgCarrierCountCommand.NotifyCanExecuteChanged();
        SupplyToBufferCommand.NotifyCanExecuteChanged();
        BufferToPlacementCommand.NotifyCanExecuteChanged();
    }

    public void RefreshEquipmentState()
    {
        _state.Refresh();
        RefreshIo();

        var pcbSupply = _pcbSupplyMotion.GetPosition();
        var pcbPlacement = _pcbPlacementMotion.GetPosition();
        var boltFastening = _boltFasteningMotion.GetPosition();
        var ngTransfer = _ngTransferMotion.GetPosition();
        PcbSupplyPosition = FormatXzPosition(pcbSupply.X, pcbSupply.Z);
        PcbPlacementPosition = FormatPosition(
            pcbPlacement.X,
            pcbPlacement.Y,
            pcbPlacement.Z);
        BoltFasteningPosition = FormatPosition(
            boltFastening.X,
            boltFastening.Y,
            boltFastening.Z);
        NgTransferPosition = FormatPosition(
            ngTransfer.X,
            ngTransfer.Y,
            ngTransfer.Z);
        UpdateSupplyMap(pcbSupply);
        UpdatePlacementMap(pcbPlacement);
        UpdateBoltMap(boltFastening);
        UpdateNgTransferMap(ngTransfer);
        UpdateDisplayStates();
    }

}
