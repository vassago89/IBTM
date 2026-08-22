using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class OperationViewModel : ObservableObject
{
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly PcbSupplyProcess _pcbSupply;
    private readonly PcbPlacementProcess _pcbPlacement;
    private readonly Recipe _recipe;
    private readonly IIoService _io;
    private readonly IReadOnlyDictionary<MachineAxis, AxisHardware> _axes;
    private readonly IAxisMotion _pcbSupplyMotion;
    private readonly IXyMotion _pcbPlacementMotion;
    private readonly IXyMotion _boltFasteningMotion;
    private readonly IXyMotion _inspectionGantryMotion;

    public OperationViewModel(
        MachineState state,
        DriverSettings drivers,
        MachineController machine,
        PcbSupplyProcess pcbSupply,
        PcbPlacementProcess pcbPlacement,
        Recipe recipe,
        IIoService io,
        IReadOnlyDictionary<MachineAxis, AxisHardware> axes,
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion pcbSupplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion boltFasteningMotion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspectionGantryMotion)
    {
        _state = state;
        _machine = machine;
        _pcbSupply = pcbSupply;
        _pcbPlacement = pcbPlacement;
        _recipe = recipe;
        _io = io;
        Driver = drivers.Control;
        _axes = axes;
        _pcbSupplyMotion = pcbSupplyMotion;
        _pcbPlacementMotion = pcbPlacementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionGantryMotion = inspectionGantryMotion;
        pcbSupplyMotion.PositionChanged += OnPcbSupplyPositionChanged;
        pcbPlacementMotion.PositionChanged += OnPcbPlacementPositionChanged;
        boltFasteningMotion.PositionChanged += OnBoltFasteningPositionChanged;
        inspectionGantryMotion.PositionChanged += OnInspectionGantryPositionChanged;
        state.Changed += OnMachineStateChanged;
        SupplyToBufferCommand.PropertyChanged += (_, _) =>
            RunOnUi(StartCommand.NotifyCanExecuteChanged);
        BufferToPlacementCommand.PropertyChanged += (_, _) =>
            RunOnUi(StartCommand.NotifyCanExecuteChanged);
    }

    public string PcbSupplyPosition
        => FormatPosition(_pcbSupplyMotion.GetPosition());

    public string PcbPlacementPosition =>
        FormatPosition(_pcbPlacementMotion.GetPosition());
    public string BoltFasteningPosition =>
        FormatPosition(_boltFasteningMotion.GetPosition());
    public string InspectionGantryPosition =>
        FormatXyPosition(_inspectionGantryMotion.GetPosition());

    public double PcbSupplyMapLeft => MapAxis(
        _pcbSupplyMotion.GetPosition().X,
        MachineAxis.PcbSupplyX,
        112,
        218);
    public double PcbSupplyMapTop => MapAxis(
        _pcbSupplyMotion.GetPosition().Y,
        MachineAxis.PcbSupplyY,
        106,
        270);
    public double PcbPlacementMapLeft => MapAxis(
        _pcbPlacementMotion.GetPosition().X,
        MachineAxis.PcbPlacementHandlerX,
        438,
        286);
    public double PcbPlacementMapTop => MapAxis(
        _pcbPlacementMotion.GetPosition().Y,
        MachineAxis.PcbPlacementHandlerY,
        106,
        312);
    public double BoltFasteningMapLeft => MapAxis(
        _boltFasteningMotion.GetPosition().X,
        MachineAxis.BoltFasteningX,
        20,
        300);
    public double BoltFasteningMapTop => MapAxis(
        _boltFasteningMotion.GetPosition().Y,
        MachineAxis.BoltFasteningY,
        110,
        286);
    public double InspectionGantryMapLeft => MapAxis(
        _inspectionGantryMotion.GetPosition().X,
        MachineAxis.InspectionGantryX,
        18,
        172);
    public double InspectionGantryMapTop => MapAxis(
        _inspectionGantryMotion.GetPosition().Y,
        MachineAxis.InspectionGantryY,
        110,
        286);

    public bool PcbSupplyPcbDetected =>
        Input(InputIo.PcbSupplyPcbDetected);
    public bool PcbPlacementPcbDetected =>
        Input(InputIo.PcbPlacementPcbDetected);
    public bool PcbSupplyIpmFixed =>
        Input(InputIo.PcbSupplyIpmFixerForward);
    public bool PcbPlacementIpmGripperClosed =>
        Input(InputIo.PcbPlacementIpmGripperClosed);
    public bool PcbPlacementStopperUp =>
        Input(InputIo.PcbPlacementStopperUp);
    public bool PcbPlacementBackupPlateUp =>
        Input(InputIo.PcbPlacementBackupPlateUp);
    public bool PcbSupplyRotated =>
        Input(InputIo.PcbSupplyRotated);
    public bool PcbSupplyAvailableFromFront1 =>
        Input(InputIo.PcbSupplyAvailableFromFront1);
    public bool PcbBufferPcbPresent =>
        Input(InputIo.PcbBufferPcbPresent);
    public bool PcbPlacementHousing1Present =>
        Input(InputIo.PcbPlacementHousing1Present);
    public bool PcbPlacementHousing2Present =>
        Input(InputIo.PcbPlacementHousing2Present);
    public bool PcbPlacementCarrierJigPresent =>
        Input(InputIo.PcbPlacementCarrierJigPresent);
    public bool BoltFasteningHousing1Present =>
        Input(InputIo.BoltFasteningHousing1Present);
    public bool BoltFasteningHousing2Present =>
        Input(InputIo.BoltFasteningHousing2Present);
    public bool BoltFasteningCarrierJigPresent =>
        Input(InputIo.BoltFasteningCarrierJigPresent);
    public bool BoltFasteningStopperUp =>
        Input(InputIo.BoltFasteningStopperUp);
    public bool BoltFasteningBackupPlateUp =>
        Input(InputIo.BoltFasteningBackupPlateUp);
    public bool InspectionHousing1Present =>
        Input(InputIo.InspectionHousing1Present);
    public bool InspectionHousing2Present =>
        Input(InputIo.InspectionHousing2Present);
    public bool InspectionCarrierJigPresent =>
        Input(InputIo.InspectionCarrierJigPresent);
    public bool InspectionStopperUp =>
        Input(InputIo.InspectionStopperUp);
    public bool InspectionBackupPlateUp =>
        Input(InputIo.InspectionBackupPlateUp);
    public bool BoltHead1Down =>
        Input(InputIo.BoltHead1Down);
    public bool BoltHead2Down =>
        Input(InputIo.BoltHead2Down);
    public bool NgCarrierGripperClosed =>
        Input(InputIo.NgCarrierGripperClosed);
    public bool NgCarrierJigDetected =>
        Input(InputIo.NgCarrierJigDetected);
    public bool NgShuttleCarrierDetected =>
        Input(InputIo.NgShuttleCarrierDetected);
    public bool NgConveyorPosition1Occupied =>
        Input(InputIo.NgConveyorPosition1Occupied);
    public bool NgConveyorPosition2Occupied =>
        Input(InputIo.NgConveyorPosition2Occupied);
    public bool NgConveyorPosition3Occupied =>
        Input(InputIo.NgConveyorPosition3Occupied);
    public bool IsHoming => _state.IsHoming;
    public ControlDriver Driver { get; }
    public bool ConveyorRunning => _state.ConveyorRunning;
    public bool BufferOccupied => _state.BufferOccupied;
    public bool BoltFasteningMoving => _boltFasteningMotion.IsMoving;
    public bool InspectionGantryMoving => _inspectionGantryMotion.IsMoving;
    public bool PcbSupplyActive =>
        _pcbSupplyMotion.IsMoving
        || _state.SupplyInBufferArea;
    public bool PcbPlacementActive =>
        _pcbPlacementMotion.IsMoving
        || _state.PlacementInBufferArea;
    public bool BufferConflict => _state.BufferConflict;
    public bool PcbPlacementHasHousing =>
        HasHousing(
            InputIo.PcbPlacementHousing1Present,
            InputIo.PcbPlacementHousing2Present);
    public bool BoltFasteningHasHousing =>
        HasHousing(
            InputIo.BoltFasteningHousing1Present,
            InputIo.BoltFasteningHousing2Present);
    public bool InspectionHasHousing =>
        HasHousing(
            InputIo.InspectionHousing1Present,
            InputIo.InspectionHousing2Present);
    public bool EmergencyStopReleased => _state.EmergencyStopReleased;
    public bool DoorClosed => _state.DoorClosed;
    public bool AirPressureOk => _state.AirPressureOk;
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync(CancellationToken cancellationToken) =>
        _machine.StartAsync(cancellationToken);

    [RelayCommand]
    private void Stop()
    {
        StartCommand.Cancel();
        _machine.Stop();
    }

    [RelayCommand]
    private void EStop() => _machine.EmergencyStop();

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => _machine.Reset();

    [RelayCommand(CanExecute = nameof(CanHome))]
    private Task HomeAsync(CancellationToken cancellationToken) =>
        _machine.HomeAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStopHome))]
    private void StopHome() => HomeCommand.Cancel();

    [RelayCommand(CanExecute = nameof(CanSupplyToBuffer))]
    private async Task SupplyToBufferAsync()
    {
        try
        {
            await _pcbSupply.MoveToBufferAsync();
        }
        catch (IoTimeoutException)
        {
            _state.SetError(MachineAlarm.Supply);
        }
    }

    [RelayCommand(CanExecute = nameof(CanBufferToPlacement))]
    private async Task BufferToPlacementAsync()
    {
        try
        {
            await _pcbPlacement.PickFromBufferAsync(
                _recipe.PcbPlacement);
        }
        catch (IoTimeoutException)
        {
            _state.SetError(MachineAlarm.Placement);
        }
    }

    private bool CanStart() =>
        _machine.CanStart
        && !SupplyToBufferCommand.IsRunning
        && !BufferToPlacementCommand.IsRunning;
    private bool CanReset() => _machine.CanReset;
    private bool CanHome() => _machine.CanHome;
    private bool CanStopHome() => _state.IsHoming;
    private bool CanSupplyToBuffer() =>
        !_state.AutomaticRunning
        && _state.CanOperate
        && _pcbSupply.CanMoveToBuffer;
    private bool CanBufferToPlacement() =>
        !_state.AutomaticRunning
        && _state.CanOperate
        && _pcbPlacement.CanPickFromBuffer;

    private void NotifyCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        StopHomeCommand.NotifyCanExecuteChanged();
        SupplyToBufferCommand.NotifyCanExecuteChanged();
        BufferToPlacementCommand.NotifyCanExecuteChanged();
    }

    private bool Input(InputIo input) => _io.GetInput(input);

    private bool HasHousing(InputIo housing1, InputIo housing2) =>
        Input(housing1) || Input(housing2);
}
