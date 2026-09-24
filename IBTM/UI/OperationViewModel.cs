using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public sealed record DoorSensorDisplay(string Name, IoInputStatus Input);

public partial class OperationViewModel : ObservableObject
{
    private readonly MachineOptions _options;
    private readonly RecipeManager _recipes;
    private readonly MachineMap _map;
    private volatile bool _active;
    private const int PcbHistoryPageSize = 100;
    private readonly MachineStore _store;
    private readonly PcbHistorySettings _historySettings;
    private readonly ILogger<OperationViewModel> _log;
    private string _pcbHistoryDirectory;
    private int _pcbHistoryLimit;
    private bool _pcbHistoryLoaded;

    public OperationViewModel(
        MachineState state,
        IoSignals signals,
        MachineController machine,
        UnitSettings units,
        MachineOptions options,
        RecipeManager recipes,
        MachineMap map,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        PcbSupplier supply,
        PcbPlacer placement,
        BoltFasteningStation fastening,
        InspectionStation inspectionStation,
        MachineStore store,
        PcbHistorySettings historySettings,
        PcbDetailsViewModel pcbDetails,
        ILogger<OperationViewModel> log)
    {
        StartCommand = new AsyncRelayCommand(StartAsync);
        StopCommand = new AsyncRelayCommand(StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        HomeCommand = new AsyncRelayCommand(HomeAsync);
        LoadOlderPcbsCommand = new AsyncRelayCommand(LoadOlderPcbsAsync, () => HasOlderPcbs);
        RetryPcbSaveCommand = new AsyncRelayCommand(RetryPcbSaveAsync);
        ClosePcbDetailsCommand = new RelayCommand(ClosePcbDetails);
        PcbRecords = new();
        _pcbHistoryLimit = PcbHistoryPageSize;
        _store = store;
        _historySettings = historySettings;
        _pcbHistoryDirectory = historySettings.Directory;
        _log = log;
        PcbDetails = pcbDetails;
        HasOlderPcbs = true;

        State = state;
        Signals = signals;
        DoorSensors = [
            new("DOOR 1", signals.Inputs[InputIo.Door1Open]),
            new("DOOR 2", signals.Inputs[InputIo.Door2Open]),
            new("DOOR 3", signals.Inputs[InputIo.Door3Open]),
            new("DOOR 4", signals.Inputs[InputIo.Door4Open]),
            new("DOOR 5", signals.Inputs[InputIo.Door5Open]),
            new("DOOR 6", signals.Inputs[InputIo.Door6Open]),
        ];
        Machine = machine;
        Inspection = inspectionStation;
        Units = units;
        _options = options;
        _recipes = recipes;
        _map = map;
        NgConveyor = ngConveyor;
        Conveyor = conveyor;
        Supply = supply;
        Placement = placement;
        Fastening = fastening;
        machine.PcbHistory.Saved += OnPcbSaved;
        machine.PcbHistory.ImageSaved += OnPcbImageSaved;

        supply.Motion.PropertyChanged += OnPcbSupplyMotionChanged;
        foreach (var axis in supply.Motion.Axes.Values)
            axis.PropertyChanged += OnPcbSupplyMotionChanged;
        // Supply/Placement also publish their handoff phase changes through Changed.
        supply.Changed += OnPcbSupplyChanged;
        placement.Motion.PropertyChanged += OnPcbPlacementMotionChanged;
        foreach (var axis in placement.Motion.Axes.Values)
            axis.PropertyChanged += OnPcbPlacementMotionChanged;
        placement.Changed += OnPcbPlacementChanged;
        fastening.Motion.PropertyChanged += OnBoltFasteningMotionChanged;
        foreach (var axis in fastening.Motion.Axes.Values)
            axis.PropertyChanged += OnBoltFasteningMotionChanged;
        fastening.Changed += OnBoltFasteningChanged;
        fastening.StepChanged += OnBoltFasteningChanged;
        inspectionStation.Motion.PropertyChanged += OnInspectionGantryMotionChanged;
        foreach (var axis in inspectionStation.Motion.Axes.Values)
            axis.PropertyChanged += OnInspectionGantryMotionChanged;
        conveyor.Changed += OnMainConveyorChanged;
        conveyor.StepChanged += OnMainConveyorChanged;
        inspectionStation.InspectionCaptured += OnInspectionCaptured;
        ngConveyor.Changed += OnNgConveyorChanged;
        ngConveyor.StepChanged += OnNgConveyorChanged;
        inspectionStation.Changed += OnInspectionChanged;
        inspectionStation.StepChanged += OnInspectionChanged;
        state.PropertyChanged += OnMachineStateChanged;
        machine.PropertyChanged += OnMachinePropertyChanged;
        recipes.Changed += OnRecipeChanged;
        signals.Outputs[OutputIo.MainConveyorRun].PropertyChanged += OnConveyorOutputChanged;
        signals.Outputs[OutputIo.NgConveyorRun].PropertyChanged += OnConveyorOutputChanged;
    }

    public MachineController Machine { get; }

    [ObservableProperty]
    public partial BitmapSource? InspectionImage { get; private set; }

    [ObservableProperty]
    public partial string? InspectionImageCaption { get; private set; }

    public MachineState State { get; }

    public IoSignals Signals { get; }

    public IReadOnlyList<DoorSensorDisplay> DoorSensors { get; }

    public UnitSettings Units { get; }

    public MainConveyor Conveyor { get; }

    public NgCarrierConveyor NgConveyor { get; }

    public InspectionStation Inspection { get; }

    public PcbSupplier Supply { get; }

    public PcbPlacer Placement { get; }

    public BoltFasteningStation Fastening { get; }

    public double? PcbSupplyMapLeft => _map.GetSupplyPosition(Supply.Motion.Position)?.X;

    public double? PcbSupplyMapTop => _map.GetSupplyPosition(Supply.Motion.Position)?.Y;

    public double? PcbPlacementMapLeft => _map.GetPlacementPosition(Placement.Motion.Position)?.X;

    public double? PcbPlacementMapTop => _map.GetPlacementPosition(Placement.Motion.Position)?.Y;

    public double? ShootingHeadMapLeft => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Shooting)?.X;

    public double? ShootingHeadMapTop => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Shooting)?.Y;

    public double? PickupHeadMapLeft => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Pickup)?.X;

    public double? PickupHeadMapTop => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Pickup)?.Y;

    public double? BoltPickupFeederMapLeft => _map.PickupFeederPosition?.X;

    public double? BoltPickupFeederMapTop => _map.PickupFeederPosition?.Y;

    public double? InspectionGantryMapLeft => _map.GetInspectionPosition(Inspection.Motion.Position)?.X;

    public double? InspectionGantryMapTop => _map.GetInspectionPosition(Inspection.Motion.Position)?.Y;

    public double? NgPickupMapLeft => _map.GetNgPickupPosition(Inspection.Motion.Position)?.X;

    public double? NgPickupMapTop => _map.GetNgPickupPosition(Inspection.Motion.Position)?.Y;

    public bool PcbSupplyPcbDetected => Supply.Pcb != PcbSupplyPcbState.None;

    public bool PcbPlacementPcbDetected => Placement.Pcb != PlacementPcbState.None;

    public bool PcbPlacementIpmDown => Placement.IpmLift == PlacementCylinderState.Down;

    public bool PcbSupplyGripperClosed => Supply.Gripper == PcbSupplyCylinderState.Forward;

    public bool PcbPlacementHeatSink1Completed
    {
        get
        {
            return Units.PcbPlacement
                && Placement.Station.CarrierPresent
                && Placement.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1)
                && HasAssembly(Placement.Station, HeatSinkSlot.HeatSink1);
        }
    }

    public bool PcbPlacementHeatSink2Completed
    {
        get
        {
            return Units.PcbPlacement
                && Placement.Station.CarrierPresent
                && Placement.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2)
                && HasAssembly(Placement.Station, HeatSinkSlot.HeatSink2);
        }
    }

    public BoltPoint? BoltFasteningActiveBolt
    {
        get
        {
            return State.AutomaticRunning && FasteningState is { } state
                ? Fastening.GetActiveBolt(state) : null;
        }
    }

    public BoltPoint? InspectionActiveBolt
    {
        get
        {
            return InspectionStateVisible && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running
                ? Inspection.GetActiveBolt(running) : null;
        }
    }

    public HeatSinkSlot? InspectionActivePcb
    {
        get
        {
            return InspectionStateVisible && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running
                ? Inspection.GetActivePcb(running) : null;
        }
    }

    public string? InspectionActiveBarcode => InspectionActivePcb is { } pcb ? InspectionBarcode(pcb) : null;

    public string? InspectionPcb1Barcode => InspectionBarcode(HeatSinkSlot.HeatSink1);

    public string? InspectionPcb2Barcode => InspectionBarcode(HeatSinkSlot.HeatSink2);

    public IReadOnlyList<BoltTargetView> BoltTargets
    {
        get
        {
            if (!Units.BoltFastening || !_map.FasteningDefined)
                return [];

            var active = BoltFasteningActiveBolt;
            var assemblies = Fastening.Station.Assemblies;
            var targets = new List<BoltTargetView>();
            foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
            {
                if (!Fastening.Station.IsHeatSinkPresent(bolt.HeatSink)
                    || _map.GetFasteningTargetPosition(bolt) is not { } position)
                    continue;

                var assembly = assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
                var results = bolt.Head == FasteningHead.Shooting ? assembly?.PcbBoltResults : assembly?.PickupBoltResults;
                var state = BoltTargetState.Pending;
                if (bolt == active)
                    state = BoltTargetState.Active;
                else if (results is not null && results.TryGetValue(bolt.Number, out var result))
                    state = result.Success ? BoltTargetState.Ok : BoltTargetState.Ng;
                targets.Add(new(bolt.Number, bolt.Head, position.X, position.Y, state));
            }
            return targets;
        }
    }

    public IReadOnlyList<BoltTargetView> InspectionTargets
    {
        get
        {
            if (!Units.Inspection || !_map.InspectionDefined)
                return [];

            var active = InspectionActiveBolt;
            var assemblies = Inspection.Station.Assemblies;
            var targets = new List<BoltTargetView>();
            foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
            {
                if (!Inspection.Station.IsHeatSinkPresent(bolt.HeatSink)
                    || _map.GetInspectionTargetPosition(bolt) is not { } position)
                    continue;

                var assembly = assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
                var state = BoltTargetState.Pending;
                if (bolt == active)
                    state = BoltTargetState.Active;
                else if (assembly is not null && assembly.BoltPresenceResults.TryGetValue(bolt.Number, out var present))
                    state = present ? BoltTargetState.Ok : BoltTargetState.Ng;
                targets.Add(new(bolt.Number, bolt.Head, position.X, position.Y, state));
            }
            return targets;
        }
    }

    public AssemblyResult BoltFasteningHeatSink1Result => GetAssemblyResult(Fastening.Station, HeatSinkSlot.HeatSink1, inspection: false);

    public AssemblyResult BoltFasteningHeatSink2Result => GetAssemblyResult(Fastening.Station, HeatSinkSlot.HeatSink2, inspection: false);

    public AssemblyResult InspectionHeatSink1Result => GetAssemblyResult(Inspection.Station, HeatSinkSlot.HeatSink1, inspection: true);

    public AssemblyResult InspectionHeatSink2Result => GetAssemblyResult(Inspection.Station, HeatSinkSlot.HeatSink2, inspection: true);

    public string ModeText => State.Available ? (State.AutoMode ? "AUTO" : "MANUAL") : "UNKNOWN";

    public bool HasAlarm
    {
        get
        {
            return State.Alarm != MachineAlarm.None
                || State.ReadError is not null;
        }
    }

    public Enum Alarm
    {
        get
        {
            return State.ReadError is null
                ? State.Alarm
                : MachineDisplayState.Unavailable;
        }
    }

    public string? AlarmDetail => State.ReadError?.ToString() ?? State.AlarmDetail;

    public string? AlarmMessage => State.ReadError?.Message ?? State.AlarmMessage;

    public bool SafetyBypass
    {
        get
        {
            return !_options.UseEmergencyStop
                || !_options.UseDoorInterlock
                || !_options.UseAirPressureInterlock;
        }
    }

    private string? InspectionBarcode(HeatSinkSlot pcb)
    {
        if (!Inspection.Station.CarrierPresent || !Inspection.Station.IsHeatSinkPresent(pcb))
            return null;
        var assembly = Inspection.Station.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == pcb);
        return assembly?.PcbBarcodeResult == AssemblyResult.Ng ? "NG · Not Read" : assembly?.PcbBarcode;
    }

    public void Activate()
    {
        _active = true;
        if (_pcbHistoryDirectory != _historySettings.Directory)
        {
            _pcbHistoryDirectory = _historySettings.Directory;
            PcbRecords.Clear();
            SelectedPcb = null;
            HasOlderPcbs = true;
            _pcbHistoryLimit = PcbHistoryPageSize;
            _pcbHistoryLoaded = false;
        }
        if (!_pcbHistoryLoaded && LoadOlderPcbsCommand.CanExecute(null))
            LoadOlderPcbsCommand.Execute(null);
        OnMachineStateChanged(this, new(null));
        OnRecipeChanged();
        OnPropertyChanged(nameof(ConveyorState));
        OnPropertyChanged(nameof(Conveyor));
        OnPropertyChanged(nameof(Units));
        OnPropertyChanged(nameof(SafetyBypass));
    }

    public void Deactivate()
    {
        _active = false;
    }

    public Task ShutdownAsync()
    {
        Deactivate();
        return CommandShutdown.CancelAndWaitAsync(
            [StopCommand, StartCommand, HomeCommand, LoadOlderPcbsCommand, RetryPcbSaveCommand]);
    }

    public IAsyncRelayCommand StartCommand { get; }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        await Machine.StartAsync(cancellationToken);
    }

    public IAsyncRelayCommand StopCommand { get; }

    private async Task StopAsync()
    {
        try
        {
            IAsyncRelayCommand[] commands = [StartCommand, HomeCommand];
            var pending = CommandShutdown.Capture(commands);
            await CommandShutdown.CancelAndWaitAsync(
                commands,
                Machine.StopAsync(),
                pending);
        }
        catch (Exception exception)
        {
            if (!State.IsError)
                State.SetError(MachineAlarm.StopFailed, exception);
            else
                _log.LogError(exception, "Machine STOP also failed.");
        }
    }

    public IAsyncRelayCommand HomeCommand { get; }

    private async Task HomeAsync(CancellationToken cancellationToken)
    {
        await Machine.HomeAsync(cancellationToken);
    }

    private static bool HasAssembly(ConveyorStation station, HeatSinkSlot heatSink)
    {
        return station.Assemblies.Any(assembly => assembly.HeatSink == heatSink);
    }

    private static AssemblyResult GetAssemblyResult(ConveyorStation station, HeatSinkSlot heatSink, bool inspection)
    {
        var assembly = station.Assemblies.FirstOrDefault(item => item.HeatSink == heatSink);
        switch (true)
        {
            case true when assembly is null:
                return AssemblyResult.Pending;
            case true when inspection:
                return assembly.InspectionResult;
            default:
                return assembly.FasteningResult;
        }
    }

    private void OnPcbSupplyMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(AxisStatus.State))
            OnPcbSupplyChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(PcbSupplyMapLeft));
            OnPropertyChanged(nameof(PcbSupplyMapTop));
        }
    }

    private void OnPcbPlacementMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(AxisStatus.State))
            OnPcbPlacementChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(PcbPlacementMapLeft));
            OnPropertyChanged(nameof(PcbPlacementMapTop));
        }
    }

    private void OnBoltFasteningMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(AxisStatus.State))
            OnBoltFasteningChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(ShootingHeadMapLeft));
            OnPropertyChanged(nameof(ShootingHeadMapTop));
            OnPropertyChanged(nameof(PickupHeadMapLeft));
            OnPropertyChanged(nameof(PickupHeadMapTop));
        }
    }

    private void OnInspectionGantryMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(MotionStatus.Position) or nameof(AxisStatus.State))
        {
            OnInspectionChanged();
            OnMainConveyorChanged();
        }

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(InspectionGantryMapLeft));
            OnPropertyChanged(nameof(InspectionGantryMapTop));
            OnPropertyChanged(nameof(NgPickupMapLeft));
            OnPropertyChanged(nameof(NgPickupMapTop));
        }
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MachineState.Available) or nameof(MachineState.SafetyReady)
            or nameof(MachineState.Alarm) or nameof(MachineState.Faulted) or nameof(MachineState.IsHoming)
            or nameof(MachineState.ServoPowerOn) or nameof(MachineState.Homed) or nameof(MachineState.IsRunning))
            OnPropertyChanged(nameof(MachineDisplayState));
        if (e.PropertyName is null or nameof(MachineState.AutoMode) or nameof(MachineState.Available))
            OnPropertyChanged(nameof(ModeText));
        if (e.PropertyName is null or nameof(MachineState.Alarm) or nameof(MachineState.ReadError))
        {
            OnPropertyChanged(nameof(HasAlarm));
            OnPropertyChanged(nameof(Alarm));
        }
        if (e.PropertyName is null or nameof(MachineState.AlarmDetail) or nameof(MachineState.ReadError))
            OnPropertyChanged(nameof(AlarmDetail));
        if (e.PropertyName is null or nameof(MachineState.AlarmMessage) or nameof(MachineState.ReadError))
            OnPropertyChanged(nameof(AlarmMessage));
        if (e.PropertyName is null or nameof(MachineState.AutomaticRunning)
            or nameof(MachineState.Alarm) or nameof(MachineState.Available))
        {
            OnPcbSupplyChanged();
            OnPcbPlacementChanged();
            OnBoltFasteningChanged();
            OnInspectionChanged();
            OnMainConveyorChanged();
            OnNgConveyorChanged();
        }
        if (e.PropertyName == nameof(MachineState.BoltTestRunning))
            OnPropertyChanged(nameof(BoltDisplayState));
        if (e.PropertyName == nameof(MachineState.RepeatEnabled))
        {
            OnInspectionChanged();
            OnMainConveyorChanged();
        }
    }

    private void OnMachinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MachineController.StartBlock)
            or nameof(MachineController.HomeBlock) or nameof(MachineController.IsStartAllowed))
        {
            OnPropertyChanged(nameof(StartBlocked));
            OnPropertyChanged(nameof(StartBlock));
        }
    }

    private void OnConveyorOutputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IoOutputStatus.IsOn))
            return;
        if (sender == Signals.Outputs[OutputIo.MainConveyorRun])
        {
            OnMainConveyorChanged();
            OnInspectionChanged();
        }
        else
        {
            OnNgConveyorChanged();
            OnInspectionChanged();
        }
    }

    private void OnRecipeChanged()
    {
        OnPropertyChanged(nameof(PcbSupplyMapLeft));
        OnPropertyChanged(nameof(PcbSupplyMapTop));
        OnPropertyChanged(nameof(PcbPlacementMapLeft));
        OnPropertyChanged(nameof(PcbPlacementMapTop));
        OnPropertyChanged(nameof(ShootingHeadMapLeft));
        OnPropertyChanged(nameof(ShootingHeadMapTop));
        OnPropertyChanged(nameof(PickupHeadMapLeft));
        OnPropertyChanged(nameof(PickupHeadMapTop));
        OnPropertyChanged(nameof(InspectionGantryMapLeft));
        OnPropertyChanged(nameof(InspectionGantryMapTop));
        OnPropertyChanged(nameof(NgPickupMapLeft));
        OnPropertyChanged(nameof(NgPickupMapTop));
        OnPropertyChanged(nameof(BoltPickupFeederMapLeft));
        OnPropertyChanged(nameof(BoltPickupFeederMapTop));
        OnPcbSupplyChanged();
        OnPcbPlacementChanged();
        OnBoltFasteningChanged();
        OnInspectionChanged();
        OnPropertyChanged(nameof(BoltFeederPositionKnown));
    }

    // Devices expose Changed events; notifying their property also refreshes nested XAML bindings.
    private void OnPcbSupplyChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(PcbSupplyPcbDetected));
        OnPropertyChanged(nameof(PcbSupplyGripperClosed));
        OnPropertyChanged(nameof(SupplyPositionKnown));
        OnPropertyChanged(nameof(Supply));
        OnPropertyChanged(nameof(SupplyDisplayState));
        OnPropertyChanged(nameof(SupplyStatus));
    }

    private void OnPcbPlacementChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(PlacementState));
        OnPropertyChanged(nameof(PlacementTarget));
        OnPropertyChanged(nameof(PlacementPositionKnown));
        OnPropertyChanged(nameof(PlacementStatus));
        OnPropertyChanged(nameof(PcbPlacementPcbDetected));
        OnPropertyChanged(nameof(PcbPlacementIpmDown));
        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(PcbPlacementHeatSink1Completed));
        OnPropertyChanged(nameof(PcbPlacementHeatSink2Completed));
        OnPropertyChanged(nameof(PlacementDisplayState));
    }

    private void OnMainConveyorChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(ConveyorState));
        OnPropertyChanged(nameof(Conveyor));
        OnPropertyChanged(nameof(ConveyorStatus));
        OnPropertyChanged(nameof(InspectionStatus));
    }

    private void OnBoltFasteningChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(FasteningState));
        OnPropertyChanged(nameof(FasteningPositionKnown));
        OnPropertyChanged(nameof(Fastening));
        OnPropertyChanged(nameof(BoltFasteningActiveBolt));
        OnPropertyChanged(nameof(BoltTargets));
        OnPropertyChanged(nameof(FasteningStateVisible));
        OnPropertyChanged(nameof(BoltFasteningHeatSink1Result));
        OnPropertyChanged(nameof(BoltFasteningHeatSink2Result));
        OnPropertyChanged(nameof(BoltDisplayState));
    }

    private void OnInspectionCaptured(ImageFrame frame, HeatSinkSlot pcb, int? boltNumber)
    {
        InspectionImageCaption = boltNumber is { } number
            ? $"{pcb.GetDescription()} · Bolt {number}"
            : $"{pcb.GetDescription()} · Data Matrix";
        InspectionImage = InspectionPreview.CreateBitmap(frame);
    }

    private void OnInspectionChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(Inspection));

        OnPropertyChanged(nameof(InspectionState));
        OnPropertyChanged(nameof(InspectionPositionKnown));
        OnPropertyChanged(nameof(InspectionActiveBolt));
        OnPropertyChanged(nameof(InspectionActivePcb));
        OnPropertyChanged(nameof(InspectionActiveBarcode));
        OnPropertyChanged(nameof(InspectionPcb1Barcode));
        OnPropertyChanged(nameof(InspectionPcb2Barcode));
        OnPropertyChanged(nameof(InspectionTargets));
        OnPropertyChanged(nameof(InspectionStateVisible));
        OnPropertyChanged(nameof(InspectionHeatSink1Result));
        OnPropertyChanged(nameof(InspectionHeatSink2Result));
        OnPropertyChanged(nameof(InspectionDisplayState));
        OnPropertyChanged(nameof(InspectionStatus));
    }

    private void OnNgConveyorChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(NgConveyorState));
        OnPropertyChanged(nameof(NgConveyor));
    }

    public MainConveyorState? ConveyorState
    {
        get
        {
            if (!State.Available
                || Signals.Outputs[OutputIo.MainConveyorRun].IsOn is not { } running)
                return null;
            return Conveyor.Step is MainConveyorState step ? step : Conveyor.GetNextStep(running, live: false);
        }
    }

    public NgConveyorState? NgConveyorState
    {
        get
        {
            if (!State.Available
                || Signals.Outputs[OutputIo.NgConveyorRun].IsOn is not { } running)
                return null;
            return NgConveyor.Step is NgConveyorState step ? step : NgConveyor.GetNextStep(running);
        }
    }

    public PcbPlacementState? PlacementState
    {
        get
        {
            if (!State.Available || !PlacementPositionKnown || !Placement.Motion.IsReady(live: false))
                return null;
            return Placement.State;
        }
    }

    public HeatSinkSlot? PlacementTarget => Placement.TargetHeatSink;

    public BoltFasteningState? FasteningState
    {
        get
        {
            if (!State.Available || !Units.BoltFastening || !Machine.TeachingReady
                || !FasteningPositionKnown || !Fastening.Motion.IsReady(live: false))
                return null;
            return Fastening.Step is BoltFasteningState step ? step : Fastening.GetNextStep(live: false);
        }
    }

    public InspectionStationState? InspectionState
    {
        get
        {
            if (!State.Available || !Units.Inspection || !Machine.TeachingReady
                || !InspectionPositionKnown || !Inspection.Motion.IsReady(live: false)
                || Signals.Outputs[OutputIo.MainConveyorRun].IsOn is not { } mainRunning
                || Signals.Outputs[OutputIo.NgConveyorRun].IsOn is not { } running)
                return null;
            return Inspection.Step is InspectionStationState step ? step
                : Inspection.GetNextStep(State.RepeatEnabled, live: false,
                    conveyorRunning: running, mainConveyorRunning: mainRunning);
        }
    }

    public bool SupplyPositionKnown
    {
        get
        {
            return Supply.Motion.XyHomed && _map.SupplyDefined
                && Supply.Motion.Position is { X: not null, Y: not null, Z: not null };
        }
    }

    public bool PlacementPositionKnown
    {
        get
        {
            return Placement.Motion.XyHomed && _map.PlacementDefined
                && Placement.Motion.Position is { X: not null, Y: not null, Z: not null };
        }
    }

    public bool FasteningPositionKnown
    {
        get
        {
            return Fastening.Motion.XyHomed && _map.FasteningDefined
                && Fastening.Motion.Position is { X: not null, Y: not null, Z: not null };
        }
    }

    public bool InspectionPositionKnown
    {
        get
        {
            return Inspection.Motion.XyHomed && _map.InspectionDefined
                && Inspection.Motion.Position is { X: not null, Y: not null };
        }
    }

    public bool BoltFeederPositionKnown => _map.PickupFeederPosition is not null;

    public Enum SupplyStatus
    {
        get
        {
            var display = SupplyDisplayState;
            return display is HandlerDisplayState.Working or HandlerDisplayState.Moving or HandlerDisplayState.Waiting
                ? Supply.State
                : display;
        }
    }

    public Enum PlacementStatus
    {
        get
        {
            var display = PlacementDisplayState;
            return display is HandlerDisplayState.Working or HandlerDisplayState.Moving or HandlerDisplayState.Waiting
                ? PlacementState ?? (Enum)MachineDisplayState.Unavailable
                : display;
        }
    }

    public Enum ConveyorStatus
    {
        get
        {
            switch (true)
            {
                case true when !Units.MainConveyor:
                    return HandlerDisplayState.Disabled;
                case true when State.Available
                    && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running:
                    return !State.AutomaticRunning && !running
                        ? HandlerDisplayState.Stopped
                        : ConveyorState ?? (Enum)MachineDisplayState.Unavailable;
                default:
                    return MachineDisplayState.Unavailable;
            }
        }
    }

    public MachineDisplayState MachineDisplayState
    {
        get
        {
            switch (State)
            {
                case { Available: false }:
                    return MachineDisplayState.Unavailable;
                case { SafetyReady: false }:
                    return MachineDisplayState.SafetyStop;
                case { Alarm: not MachineAlarm.None }:
                    return MachineDisplayState.Alarm;
                case { Faulted: true }:
                    return MachineDisplayState.MotionFault;
                case { IsHoming: true }:
                    return MachineDisplayState.Homing;
                case { ServoPowerOn: false }:
                    return MachineDisplayState.ServoOff;
                case { Homed: false }:
                    return MachineDisplayState.HomeRequired;
                case { IsRunning: true }:
                    return MachineDisplayState.Running;
                default:
                    return MachineDisplayState.Ready;
            }
        }
    }

    public bool StartBlocked
    {
        get
        {
            return !Machine.IsStartAllowed
                && !State.IsHoming
                && Machine.StartBlock != StartBlockReason.None;
        }
    }

    public bool FasteningStateVisible
    {
        get
        {
            return State.AutomaticRunning
                && FasteningState is not null and not BoltFasteningState.Waiting;
        }
    }

    public bool InspectionStateVisible
    {
        get
        {
            return State.AutomaticRunning
                && InspectionState is not null and not InspectionStationState.Waiting;
        }
    }

    public Enum StartBlock
    {
        get
        {
            return Machine.StartBlock == StartBlockReason.HomeRequired
                && Machine.HomeBlock != HomeBlockReason.None
                ? Machine.HomeBlock
                : Machine.StartBlock;
        }
    }

    public HandlerDisplayState SupplyDisplayState
    {
        get
        {
            switch (true)
            {
                case true when !Units.PcbSupply:
                    return HandlerDisplayState.Disabled;
                case true when Alarm is MachineAlarm.PcbSupply:
                    return HandlerDisplayState.IoAlarm;
                case true when !SupplyPositionKnown:
                    return HandlerDisplayState.PositionUnknown;
                case true when Supply.Motion.IsMoving:
                    return HandlerDisplayState.Moving;
                case true when !State.AutomaticRunning:
                    return HandlerDisplayState.Stopped;
                default:
                    return Supply.State is PcbSupplyState.WaitingForCarrier
                        or PcbSupplyState.WaitingForCarrierExit
                        or PcbSupplyState.HandingOff
                        or PcbSupplyState.WaitingForPlacementClear
                        or PcbSupplyState.WaitingForReturnedPcb
                        or PcbSupplyState.WaitingForReturnedPcbGrip
                        or PcbSupplyState.WaitingForReturnClear
                        ? HandlerDisplayState.Waiting
                        : HandlerDisplayState.Working;
            }
        }
    }

    public HandlerDisplayState PlacementDisplayState
    {
        get
        {
            switch (true)
            {
                case true when !Units.PcbPlacement:
                    return HandlerDisplayState.Disabled;
                case true when Alarm is MachineAlarm.PcbPlacement:
                    return HandlerDisplayState.IoAlarm;
                case true when !PlacementPositionKnown:
                    return HandlerDisplayState.PositionUnknown;
                case true when Placement.Motion.IsMoving:
                    return HandlerDisplayState.Moving;
                case true when !State.AutomaticRunning:
                    return HandlerDisplayState.Stopped;
                default:
                    return PlacementState is PcbPlacementState.WaitingForSupply
                        or PcbPlacementState.WaitingForSupplyRelease
                        or PcbPlacementState.WaitingForSupplyClear
                        or PcbPlacementState.WaitingForCarrier
                        or PcbPlacementState.WaitingForSupplyReceipt
                        or PcbPlacementState.WaitingForSupplyGrip
                        or PcbPlacementState.WaitingForSupplyDeparture
                        ? HandlerDisplayState.Waiting
                        : HandlerDisplayState.Working;
            }
        }
    }

    public StationDisplayState BoltDisplayState
    {
        get
        {
            switch (true)
            {
                case true when !Units.BoltFastening:
                    return StationDisplayState.Disabled;
                case true when Alarm is MachineAlarm.PickupBoltFeeder
                    or MachineAlarm.ShootingBoltFeeder
                    or MachineAlarm.BoltFastening:
                    return StationDisplayState.IoAlarm;
                case true when !FasteningPositionKnown:
                    return StationDisplayState.PositionUnknown;
                case true when Fastening.Motion.IsMoving || State.BoltTestRunning:
                    return StationDisplayState.Working;
                case true when !State.AutomaticRunning:
                    return StationDisplayState.Stopped;
                case true when !Fastening.Station.CarrierPresent:
                    return StationDisplayState.WaitingForCarrier;
                case true when Fastening.Station.Completed:
                    return StationDisplayState.WaitingForTransfer;
                default:
                    return FasteningStateVisible
                        ? StationDisplayState.Working
                        : StationDisplayState.HeatSinkDetected;
            }
        }
    }

    public Enum InspectionStatus
    {
        get
        {
            switch (InspectionDisplayState)
            {
                case StationDisplayState.Working when InspectionStateVisible:
                    return InspectionState ?? (Enum)MachineDisplayState.Unavailable;
                case StationDisplayState.WaitingForTransfer when Units.Inspection && Inspection.RouteToNg:
                    return InspectionStationState.WaitingForDestination;
                case StationDisplayState.WaitingForTransfer when Units.MainConveyor
                        && ConveyorState == MainConveyorState.WaitingForRearEquipment:
                    return ConveyorState ?? (Enum)MachineDisplayState.Unavailable;
                default:
                    return InspectionDisplayState;
            }
        }
    }

    public StationDisplayState InspectionDisplayState
    {
        get
        {
            switch (true)
            {
                case true when !Units.Inspection:
                    return StationDisplayState.Disabled;
                case true when Alarm is MachineAlarm.Inspection or MachineAlarm.NgCarrierTransfer:
                    return StationDisplayState.IoAlarm;
                case true when !InspectionPositionKnown:
                    return StationDisplayState.PositionUnknown;
                case true when !State.AutomaticRunning && !Inspection.Motion.IsMoving:
                    return StationDisplayState.Stopped;
                case true when Inspection.Motion.IsMoving
                    || Inspection.IsTransferPending
                    || InspectionState is InspectionStationState.PreparingTransfer
                        or InspectionStationState.PickingCarrier or InspectionStationState.PlacingCarrier
                        or InspectionStationState.WaitingForShuttleDown:
                    return StationDisplayState.Working;
                case true when !Inspection.Station.CarrierPresent:
                    return StationDisplayState.WaitingForCarrier;
                case true when Inspection.Station.Completed:
                    return StationDisplayState.WaitingForTransfer;
                default:
                    return InspectionStateVisible
                        ? StationDisplayState.Working
                        : StationDisplayState.HeatSinkDetected;
            }
        }
    }

    public ObservableCollection<PcbRecord> PcbRecords { get; }

    public IAsyncRelayCommand LoadOlderPcbsCommand { get; }

    public IAsyncRelayCommand RetryPcbSaveCommand { get; }

    public IRelayCommand ClosePcbDetailsCommand { get; }

    public PcbDetailsViewModel PcbDetails { get; }

    [ObservableProperty]
    public partial PcbRecord? SelectedPcb { get; set; }

    partial void OnSelectedPcbChanged(PcbRecord? value)
    {
        PcbDetails.Record = value;
    }

    private void OnPcbImageSaved(long number)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnPcbImageSaved(number));
            return;
        }
        if (SelectedPcb?.Number == number)
            PcbDetails.RefreshImages();
    }

    [ObservableProperty]
    public partial string? PcbHistoryError { get; private set; }

    private async Task RetryPcbSaveAsync()
    {
        try
        {
            await Machine.PcbHistory.FlushAsync();
        }
        catch (Exception exception)
        {
            // The manager retains queued data and exposes SaveError directly to the view.
            _log.LogError(exception, "PCB save retry failed.");
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderPcbsCommand))]
    public partial bool HasOlderPcbs { get; private set; }

    private async Task LoadOlderPcbsAsync(CancellationToken cancellationToken)
    {
        PcbHistoryError = null;
        var before = _pcbHistoryLoaded && PcbRecords.Count > 0 ? PcbRecords[^1].Number : (long?)null;
        var directory = _pcbHistoryDirectory;
        try
        {
            var records = await Task.Run(
                () => _store.LoadPcbs(directory, before, PcbHistoryPageSize), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (directory != _pcbHistoryDirectory)
                return;
            _pcbHistoryLimit = Math.Max(PcbHistoryPageSize,
                before.HasValue ? PcbRecords.Count + records.Count : PcbRecords.Count);
            foreach (var record in records)
                UpdatePcbRecord(record);
            HasOlderPcbs = records.Count == PcbHistoryPageSize;
            _pcbHistoryLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PcbHistoryError = $"PCB history could not be loaded: {exception.Message}";
            _log.LogError(exception, "PCB history load failed for {Directory}.", directory);
        }
    }

    private void OnPcbSaved(PcbRecord record)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnPcbSaved(record));
            return;
        }
        UpdatePcbRecord(record);
    }

    private void UpdatePcbRecord(PcbRecord record)
    {
        var index = 0;
        while (index < PcbRecords.Count && PcbRecords[index].Number > record.Number)
            index++;
        var selected = SelectedPcb?.Number == record.Number;
        if (index < PcbRecords.Count && PcbRecords[index].Number == record.Number)
        {
            if (PcbRecords[index].UpdatedAt > record.UpdatedAt)
                return;
            PcbRecords[index] = record;
        }
        else
        {
            PcbRecords.Insert(index, record);
        }
        if (selected)
            SelectedPcb = record;
        if (PcbRecords.Count > _pcbHistoryLimit)
        {
            PcbRecords.RemoveAt(PcbRecords.Count - 1);
            HasOlderPcbs = true;
        }
    }

    private void ClosePcbDetails()
    {
        SelectedPcb = null;
    }
}
