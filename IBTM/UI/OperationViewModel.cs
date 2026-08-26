using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class OperationViewModel : ObservableObject
{
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly IIoService _io;
    private readonly IReadOnlyDictionary<MachineAxis, AxisHardware> _axes;
    private readonly IAxisMotion _pcbSupplyMotion;
    private readonly IXyMotion _pcbPlacementMotion;
    private readonly IXyMotion _boltFasteningMotion;
    private readonly IXyMotion _inspectionGantryMotion;
    private readonly InspectionWork _inspectionWork;

    public OperationViewModel(
        MachineState state,
        MachineController machine,
        IIoService io,
        IReadOnlyDictionary<MachineAxis, AxisHardware> axes,
        InspectionWork inspectionWork,
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion pcbSupplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion boltFasteningMotion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspectionGantryMotion)
    {
        _state = state;
        _machine = machine;
        _io = io;
        _axes = axes;
        _inspectionWork = inspectionWork;
        _pcbSupplyMotion = pcbSupplyMotion;
        _pcbPlacementMotion = pcbPlacementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionGantryMotion = inspectionGantryMotion;
        pcbSupplyMotion.PositionChanged += OnPcbSupplyPositionChanged;
        pcbPlacementMotion.PositionChanged += OnPcbPlacementPositionChanged;
        boltFasteningMotion.PositionChanged += OnBoltFasteningPositionChanged;
        inspectionGantryMotion.PositionChanged += OnInspectionGantryPositionChanged;
        inspectionWork.Changed += OnMachineStateChanged;
        state.Changed += OnMachineStateChanged;
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
    public bool PickupFeederReady =>
        Input(InputIo.PickupFeederBoltDetected);
    public bool LinearFeederReady =>
        Input(InputIo.LinearFeederBoltDetected);
    public bool ShootingEscapeBack =>
        Input(InputIo.ShootingEscapeBackward)
        && !Input(InputIo.ShootingEscapeForward);
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
    public NgConveyorState NgConveyorState =>
        _state.NgConveyorState;
    public bool IsHoming => _state.IsHoming;
    public bool ConveyorRunning => _state.ConveyorRunning;
    public bool BoltFasteningMoving => _boltFasteningMotion.IsMoving;
    public bool InspectionGantryMoving => _inspectionGantryMotion.IsMoving;
    public bool PcbSupplyActive =>
        _pcbSupplyMotion.IsMoving
        || _state.SupplyInBufferArea;
    public bool PcbPlacementActive =>
        _pcbPlacementMotion.IsMoving
        || _state.PlacementInBufferArea;
    public bool BufferConflict => _state.BufferConflict;
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

    private bool CanStart() => _machine.CanStart;
    private bool CanReset() => _machine.CanReset;
    private bool CanHome() => _machine.CanHome;
    private bool CanStopHome() => _state.IsHoming;
    private void NotifyCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        StopHomeCommand.NotifyCanExecuteChanged();
    }

    private bool Input(InputIo input) => _io.GetInput(input);

    private bool HasHousing(InputIo housing1, InputIo housing2) =>
        Input(housing1) || Input(housing2);

    private static string FormatPosition(
        (double X, double Y, double Z) position) =>
        $"X {position.X:F3}   Y {position.Y:F3}   Z {position.Z:F3}";

    private static string FormatXyPosition(
        (double X, double Y, double Z) position) =>
        $"X {position.X:F3}   Y {position.Y:F3}";

    private void OnPcbSupplyPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(PcbSupplyPosition));
            OnPropertyChanged(nameof(PcbSupplyMapLeft));
            OnPropertyChanged(nameof(PcbSupplyMapTop));
        });

    private void OnPcbPlacementPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(PcbPlacementPosition));
            OnPropertyChanged(nameof(PcbPlacementMapLeft));
            OnPropertyChanged(nameof(PcbPlacementMapTop));
        });

    private void OnBoltFasteningPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(BoltFasteningPosition));
            OnPropertyChanged(nameof(BoltFasteningMapLeft));
            OnPropertyChanged(nameof(BoltFasteningMapTop));
        });

    private void OnInspectionGantryPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(InspectionGantryPosition));
            OnPropertyChanged(nameof(InspectionGantryMapLeft));
            OnPropertyChanged(nameof(InspectionGantryMapTop));
        });

    private void OnMachineStateChanged() =>
        RunOnUi(() =>
        {
            OnPropertyChanged(new PropertyChangedEventArgs(null));
            NotifyCanExecuteChanged();
        });

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);
}
