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

public sealed record RecentFasteningView(string BoltName, HeatSinkSlot HeatSink, FasteningHead Head, BoltResult Result);

public sealed record FasteningResumeRow(string Label, HeatSinkSlot HeatSink, BoltResult? Result)
{
    public string Status => Result is null ? "Not recorded"
        : Result.Source == BoltResultSource.DryRun ? "Dry run" : Result.Success ? "OK" : "NG";
}

public partial class OperationViewModel : ObservableObject
{
    private readonly MachineOptions _options;
    private readonly MachineDiagramMapper _map;
    private volatile bool _active;
    private readonly MachineStore _store;
    private readonly DiagnosticWindowManager _windows;
    private readonly PcbHistorySettings _historySettings;
    private readonly ILogger<OperationViewModel> _log;
    private string _pcbHistoryDirectory;
    private int _pcbHistoryLimit;
    private bool _pcbHistoryLoaded;
    private ConveyorStation.Job? _reviewedFasteningJob;

    public OperationViewModel(
        MachineState state,
        IoSignals signals,
        MachineController machine,
        UnitSettings units,
        MachineOptions options,
        RecipeManager recipes,
        MachineDiagramMapper map,
        MainConveyor conveyor,
        NgCarrierConveyor ngConveyor,
        PcbSupplier supply,
        PcbPlacer placement,
        BoltFasteningStation fastening,
        InspectionStation inspectionStation,
        MachineStore store,
        PcbHistorySettings historySettings,
        PcbResultsViewModel pcbDetails,
        DiagnosticWindowManager windows,
        ILogger<OperationViewModel> log)
    {
        ClearCountsCommand = new AsyncRelayCommand(ClearCountsAsync);
        StartCommand = new AsyncRelayCommand(StartAsync);
        ConfirmStartCommand = new AsyncRelayCommand(ConfirmStartAsync);
        CheckStartCommand = new AsyncRelayCommand(CheckStartAsync);
        SelectStartAreaCommand = new RelayCommand<StartArea>(SelectStartArea);
        ChangeCarrierWorkCommand = new AsyncRelayCommand<CarrierWorkAction>(ChangeCarrierWorkAsync);
        SetStartBackupPlateCommand = new AsyncRelayCommand<bool>(SetStartBackupPlateAsync);
        StopCommand = new AsyncRelayCommand(StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ResetCommand = new AsyncRelayCommand(ResetAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        HomeCommand = new AsyncRelayCommand(machine.HomeAsync);
        LoadOlderPcbsCommand = new AsyncRelayCommand(LoadOlderPcbsAsync);
        RetryPcbSaveCommand = new AsyncRelayCommand(RetryPcbSaveAsync);
        PcbRecords = new();
        FasteningResumeBolts = new();
        _pcbHistoryLimit = MachineStore.PcbHistoryPageSize;
        _store = store;
        _windows = windows;
        _historySettings = historySettings;
        _pcbHistoryDirectory = historySettings.Directory;
        _log = log;
        IsResetAllowed = true;
        PcbDetails = pcbDetails;
        HasOlderPcbs = true;

        State = state;
        Signals = signals;
        DoorSensors = [
            new(UiText.Get("DOOR 1"), signals.Inputs[InputIo.Door1Open]),
            new(UiText.Get("DOOR 2"), signals.Inputs[InputIo.Door2Open]),
            new(UiText.Get("DOOR 3"), signals.Inputs[InputIo.Door3Open]),
            new(UiText.Get("DOOR 4"), signals.Inputs[InputIo.Door4Open]),
            new(UiText.Get("DOOR 5"), signals.Inputs[InputIo.Door5Open]),
            new(UiText.Get("DOOR 6"), signals.Inputs[InputIo.Door6Open]),
        ];
        Machine = machine;
        Inspection = inspectionStation;
        Units = units;
        _options = options;
        Recipes = recipes;
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
        SelectedStartArea = StartArea.Station1;
    }

    public MachineController Machine { get; }

    public RecipeManager Recipes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStation), nameof(StartMotion), nameof(StartMaterialState))]
    public partial StartArea SelectedStartArea { get; private set; }

    [ObservableProperty]
    public partial string? StartActionMessage { get; private set; }

    public ConveyorStation? StartStation => SelectedStartArea switch
    {
        StartArea.Station1 => Placement.Station,
        StartArea.Station2 => Fastening.Station,
        StartArea.Station3 => Inspection.Station,
        _ => null,
    };

    public MotionStatus StartMotion => SelectedStartArea switch
    {
        StartArea.Supply => Supply.Motion,
        StartArea.Placement or StartArea.Station1 => Placement.Motion,
        StartArea.PickupHead or StartArea.ShootingHead or StartArea.Station2 => Fastening.Motion,
        _ => Inspection.Motion,
    };

    public StartCheckState StartMaterialState => Machine.StartChecks[SelectedStartArea];

    [ObservableProperty]
    public partial BitmapSource? InspectionImage { get; private set; }

    [ObservableProperty]
    public partial string? InspectionImageCaption { get; private set; }

    [ObservableProperty]
    public partial RecentFasteningView? RecentFastening { get; private set; }

    public MachineState State { get; }

    public IoSignals Signals { get; }

    public IReadOnlyList<DoorSensorDisplay> DoorSensors { get; }

    public UnitSettings Units { get; }

    private MainConveyor Conveyor { get; }

    public NgCarrierConveyor NgConveyor { get; }

    public InspectionStation Inspection { get; }

    public PcbSupplier Supply { get; }

    public PcbPlacer Placement { get; }

    public BoltFasteningStation Fastening { get; }

    public Point? PcbSupplyMapPosition => _map.GetSupplyPosition(Supply.Motion.Position);

    public Point? PcbPlacementMapPosition => _map.GetPlacementPosition(Placement.Motion.Position);

    public Point? ShootingHeadMapPosition => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Shooting);

    public Point? PickupHeadMapPosition => _map.GetFasteningPosition(Fastening.Motion.Position, FasteningHead.Pickup);

    public Point? InspectionGantryMapPosition => _map.GetInspectionPosition(Inspection.Motion.Position);

    public Point? NgPickupMapPosition => _map.GetNgPickupPosition(Inspection.Motion.Position);

    public bool PcbPlacementHeatSink1Completed
    {
        get
        {
            return Units.PcbPlacement
                && Placement.Station.CarrierPresent
                && Placement.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1)
                && Placement.Station.Assemblies.Any(assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1);
        }
    }

    public bool PcbPlacementHeatSink2Completed
    {
        get
        {
            return Units.PcbPlacement
                && Placement.Station.CarrierPresent
                && Placement.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2)
                && Placement.Station.Assemblies.Any(assembly => assembly.HeatSink == HeatSinkSlot.HeatSink2);
        }
    }

    public BoltPoint? BoltFasteningActiveBolt
    {
        get
        {
            return Fastening.IsRunning && FasteningState is not null
                ? Fastening.ActiveBolt : null;
        }
    }

    public BoltPoint? InspectionActiveBolt => InspectionStateVisible ? Inspection.ActiveBolt : null;

    public HeatSinkSlot? InspectionActivePcb => InspectionStateVisible ? Inspection.ActivePcb : null;

    public string? InspectionActiveBarcode => InspectionActivePcb is { } pcb ? InspectionBarcode(pcb) : null;

    public string? InspectionPcb1Barcode => InspectionBarcode(HeatSinkSlot.HeatSink1);

    public string? InspectionPcb2Barcode => InspectionBarcode(HeatSinkSlot.HeatSink2);

    public int? BoltFasteningActiveOrdinal
    {
        get
        {
            return BoltFasteningActiveBolt is { } bolt
                ? Recipes.Current.Pcb.GetBoltOrdinal(bolt.Id) : null;
        }
    }

    public int? InspectionActiveOrdinal
    {
        get
        {
            return InspectionActiveBolt is { } bolt
                ? Recipes.Current.Pcb.GetBoltOrdinal(bolt.Id) : null;
        }
    }

    public IReadOnlyList<BoltDiagramMarker> BoltTargets
    {
        get
        {
            if (!Units.BoltFastening || !_map.FasteningDefined)
                return [];

            var active = BoltFasteningActiveBolt;
            var assemblies = Fastening.Station.Assemblies;
            var targets = new List<BoltDiagramMarker>();
            foreach (var bolt in Recipes.Current.Pcb.BoltPoints)
            {
                if (!Fastening.Station.IsHeatSinkPresent(bolt.HeatSink)
                    || _map.GetFasteningTargetPosition(bolt) is not { } position)
                    continue;

                var assembly = assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
                var results = bolt.Head == FasteningHead.Shooting ? assembly?.ShootingBoltResults : assembly?.PickupBoltResults;
                var state = BoltTargetState.Pending;
                if (bolt.Id == active?.Id)
                    state = BoltTargetState.Active;
                else if (results is not null && results.TryGetValue(bolt.Id, out var result))
                    state = result switch
                    {
                        { Success: false } or { TurnsResult: AssemblyResult.Ng } => BoltTargetState.Ng,
                        { TurnsResult: AssemblyResult.Pending } => BoltTargetState.Pending,
                        _ => BoltTargetState.Ok,
                    };
                targets.Add(new(Recipes.Current.Pcb.GetBoltOrdinal(bolt.Id)!.Value, bolt.Head, position.X, position.Y, state));
            }
            return targets;
        }
    }

    public IReadOnlyList<BoltDiagramMarker> InspectionTargets
    {
        get
        {
            if (!Units.Inspection || !_map.InspectionDefined)
                return [];

            var active = InspectionActiveBolt;
            var assemblies = Inspection.Station.Assemblies;
            var targets = new List<BoltDiagramMarker>();
            foreach (var bolt in Recipes.Current.Pcb.BoltPoints)
            {
                if (!Inspection.Station.IsHeatSinkPresent(bolt.HeatSink)
                    || _map.GetInspectionTargetPosition(bolt) is not { } position)
                    continue;

                var assembly = assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
                var state = BoltTargetState.Pending;
                if (bolt.Id == active?.Id)
                    state = BoltTargetState.Active;
                else if (assembly is not null && assembly.BoltPresenceResults.TryGetValue(bolt.Id, out var present))
                    state = present ? BoltTargetState.Ok : BoltTargetState.Ng;
                targets.Add(new(Recipes.Current.Pcb.GetBoltOrdinal(bolt.Id)!.Value, bolt.Head, position.X, position.Y, state));
            }
            return targets;
        }
    }

    public AssemblyResult BoltFasteningHeatSink1Result => GetAssemblyResult(Fastening.Station, HeatSinkSlot.HeatSink1, inspection: false);

    public AssemblyResult BoltFasteningHeatSink2Result => GetAssemblyResult(Fastening.Station, HeatSinkSlot.HeatSink2, inspection: false);

    public AssemblyResult InspectionHeatSink1Result => GetAssemblyResult(Inspection.Station, HeatSinkSlot.HeatSink1, inspection: true);

    public AssemblyResult InspectionHeatSink2Result => GetAssemblyResult(Inspection.Station, HeatSinkSlot.HeatSink2, inspection: true);

    public string ModeText => State.Available ? (State.AutoMode ? UiText.Get("AUTO") : UiText.Get("MANUAL")) : UiText.Get("UNKNOWN");

    public bool HasAlarm => Alarm is not MachineAlarm.None;

    public Enum Alarm
    {
        get
        {
            if (State.Alarm != MachineAlarm.None)
                return State.Alarm;
            if (State.ReadError is not null)
                return MachineDisplayState.Unavailable;
            if (State.PendingStop is { } pending)
                return pending.Alarm;
            return State.Available && State.FeedbackReadiness.Faulted
                ? MachineAlarm.MotionUnavailable
                : MachineAlarm.None;
        }
    }

    public string? AlarmDetail => State.AlarmDetail ?? State.ReadError?.ToString() ?? State.PendingStop?.Error.ToString();

    public string? AlarmMessage => State.AlarmMessage ?? State.ReadError?.Message ?? State.PendingStop?.Error.Message;

    public string? AlarmAction
    {
        get
        {
            if (State.Alarm == MachineAlarm.None && State.PendingStop is not null && State.ReadError is null)
                return UiText.Get("Current work is finishing. Wait for the machine to stop before checking the indicated equipment.");
            switch (Alarm)
            {
                case MachineAlarm.EmergencyStop:
                    return UiText.Get("Check why the emergency stop was pressed. When safe, release it and press RESET.");
                case MachineAlarm.DoorOpen:
                    return UiText.Get("Check the red door indicators above. Close the doors, then press RESET.");
                case MachineAlarm.AirPressureLow:
                    return UiText.Get("Check the air supply and pressure. Restore the pressure, then press RESET.");
                case MachineAlarm.IoCommunication:
                case MachineDisplayState.Unavailable:
                    return UiText.Get("Check control I/O power and communication. Feedback is unavailable. After recovery, press RESET.");
                case MachineAlarm.MotionUnavailable:
                    return UiText.Get("Open MOTION to check the axis alarm and servo state. Correct the cause, then press RESET.");
                case MachineAlarm.HomeFailed:
                    return UiText.Get("Check the axis alarm, home sensor and travel path in MOTION. Correct the cause, then retry homing from MANUAL or Start Review.");
                case MachineAlarm.PcbSupply:
                    return UiText.Get("Check the PCB supply gripper, rotation and PCB position. Correct the cause, then press RESET and verify the material in Start Review.");
                case MachineAlarm.PcbPlacement:
                    return UiText.Get("Check placement vacuum, handler lift and PCB position. Correct the cause, then press RESET and verify the carrier in Start Review.");
                case MachineAlarm.PickupBoltFeeder:
                    return UiText.Get("Check pickup feeder bolts, jams and the bolt sensor. Stop motion before entering the machine. After service, press RESET and review unfinished carriers.");
                case MachineAlarm.ShootingBoltFeeder:
                    return UiText.Get("Check shooting feeder bolts, the tube and cylinder feedback. Stop motion before entering the machine. After service, press RESET and review unfinished carriers.");
                case MachineAlarm.BoltFastening:
                    return UiText.Get("Check the fastening controller alarm, bolt pickup and head cylinders. Correct the cause, then press RESET and review the remaining bolts in Start Review.");
                case MachineAlarm.Inspection:
                    return UiText.Get("Check the camera, lighting and inspection mechanism using the fault details. Correct the cause, then press RESET and review the carrier.");
                case MachineAlarm.NgCarrierTransfer:
                    return UiText.Get("Check the NG pickup gripper, lift and carrier support. Correct the cause, then press RESET and review the handler in Start Review.");
                case MachineAlarm.NgShuttle:
                    return UiText.Get("Check the shuttle lift sensors, carrier and inspection handler clearance. Correct the cause, then press RESET.");
                case MachineAlarm.MainConveyor:
                    return UiText.Get("Check the stopped carrier, arrival sensors, backup plates and stoppers. Correct the cause, then press RESET and review each carrier in Start Review.");
                case MachineAlarm.NgConveyor:
                    return UiText.Get("Check the NG carrier position, sensors, stopper and conveyor drive. Correct the cause, then press RESET. This is separate from the normal unloading request.");
                case MachineAlarm.StopFailed:
                    return UiText.Get("Motion may not have stopped. Press the emergency stop and confirm all motion has stopped before checking the equipment.");
                default:
                    return null;
            }
        }
    }

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
        return assembly?.PcbBarcodeResult == AssemblyResult.Ng ? UiText.Get("NG · Not Read") : assembly?.PcbBarcode;
    }

    public void Activate()
    {
        _active = true;
        if (_pcbHistoryDirectory != _historySettings.Directory)
        {
            _pcbHistoryDirectory = _historySettings.Directory;
            PcbRecords.Clear();
            PcbDetails.Record = null;
            PcbHistoryError = null;
            HasOlderPcbs = true;
            _pcbHistoryLimit = MachineStore.PcbHistoryPageSize;
            _pcbHistoryLoaded = false;
        }
        if (!_pcbHistoryLoaded && LoadOlderPcbsCommand.CanExecute(null))
            LoadOlderPcbsCommand.Execute(null);
        OnMachineStateChanged(this, new(null));
        OnRecipeChanged();
        OnPropertyChanged(nameof(Units));
        OnPropertyChanged(nameof(SafetyBypass));
    }

    public void Deactivate()
    {
        _active = false;
    }

    public Task ShutdownAsync()
    {
        IsResetAllowed = false;
        Deactivate();
        return CommandShutdown.CancelAndWaitAsync(
            [StopCommand, ResetCommand, StartCommand, ConfirmStartCommand, CheckStartCommand, ChangeCarrierWorkCommand,
                SetStartBackupPlateCommand, HomeCommand, LoadOlderPcbsCommand, RetryPcbSaveCommand, ClearCountsCommand]);
    }

    public ObservableCollection<FasteningResumeRow> FasteningResumeBolts { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartReviewAllowed))]
    public partial bool IsFasteningResumeConfirmed { get; set; }

    public int RemainingFasteningCount => FasteningResumeBolts.Count(row => row.Result is null);

    public bool IsFasteningResumeAvailable => State.Available && !State.IsRunning
        && _reviewedFasteningJob is { } job && Machine.IsFasteningResumeAllowed(job)
        && Enum.GetValues<HeatSinkSlot>().All(pcb => Fastening.Station.IsHeatSinkPresent(pcb)
            == FasteningResumeBolts.Any(row => row.HeatSink == pcb));

    private void RefreshFasteningResume()
    {
        // Every explicit check requires a new operator confirmation; sensor edges cannot grant it.
        IsFasteningResumeConfirmed = false;
        _reviewedFasteningJob = Fastening.Station.CurrentJob;
        FasteningResumeBolts.Clear();
        foreach (var bolt in Recipes.Current.Pcb.FasteningPoints.Where(bolt => Fastening.Station.IsHeatSinkPresent(bolt.HeatSink)))
        {
            var assembly = Fastening.Station.Assemblies.FirstOrDefault(item => item.HeatSink == bolt.HeatSink);
            var result = assembly?.ShootingBoltResults.GetValueOrDefault(bolt.Id)
                ?? assembly?.PickupBoltResults.GetValueOrDefault(bolt.Id);
            FasteningResumeBolts.Add(new(Recipes.Current.Pcb.GetBoltName(bolt.Id), bolt.HeatSink, result));
        }
        OnPropertyChanged(nameof(RemainingFasteningCount));
        OnPropertyChanged(nameof(IsFasteningResumeAvailable));
        OnPropertyChanged(nameof(IsStartReviewAllowed));
    }

    public IAsyncRelayCommand ClearCountsCommand { get; }

    private async Task ClearCountsAsync()
    {
        try
        {
            await Recipes.ClearProductionCountsAsync();
            PcbHistoryError = null;
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "Production count reset failed.");
            PcbHistoryError = UiText.Format($"Count reset failed: {exception.Message}");
        }
    }

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand ConfirmStartCommand { get; }

    public IAsyncRelayCommand CheckStartCommand { get; }

    public IRelayCommand<StartArea> SelectStartAreaCommand { get; }
    public IAsyncRelayCommand<CarrierWorkAction> ChangeCarrierWorkCommand { get; }
    public IAsyncRelayCommand<bool> SetStartBackupPlateCommand { get; }

    private Task SetStartBackupPlateAsync(bool up, CancellationToken cancellationToken)
    {
        OutputIo? output = SelectedStartArea switch
        {
            StartArea.Station1 => OutputIo.PcbPlacementBackupPlateUp,
            StartArea.Station2 => OutputIo.BoltFasteningBackupPlateUp,
            StartArea.Station3 => OutputIo.InspectionBackupPlateUp,
            _ => null,
        };
        if (output is not { } signal)
            return Task.CompletedTask;
        StartActionMessage = null;
        return Machine.SetTeachingOutputAsync(Signals.Outputs[signal], cancellationToken, requestedValue: up);
    }

    private void SelectStartArea(StartArea area)
    {
        SelectedStartArea = area;
        StartActionMessage = null;
    }

    private async Task ChangeCarrierWorkAsync(CarrierWorkAction action, CancellationToken cancellationToken)
    {
        var area = SelectedStartArea;
        var job = StartStation?.CurrentJob;
        if (job is null)
            return;
        try
        {
            await Task.Run(() => Machine.ChangeCarrierWork(area, job, action, cancellationToken), cancellationToken);
            RefreshFasteningResume();
            StartActionMessage = $"{UiText.Get(area)} · {UiText.Get(action == CarrierWorkAction.Complete ? "Marked complete" : "Results cleared")}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _log.LogError(exception, "Carrier work change failed: {Area}, {Action}.", area, action);
            StartActionMessage = UiText.Get(exception.Message);
        }
        finally
        {
            OnPropertyChanged(nameof(StartStation));
        }
    }

    public bool IsStartReviewAllowed => Machine.IsStartAllowed
        && Machine.StartBlock is StartBlockReason.None or StartBlockReason.UnfinishedCarrier
        && Machine.StartChecks.All(check => check.Value is not (StartCheckState.NotChecked or StartCheckState.Unknown
            or StartCheckState.MaterialRemaining or StartCheckState.UnfinishedCarrier)
            || check.Key == StartArea.Station2 && check.Value == StartCheckState.UnfinishedCarrier
                && IsFasteningResumeConfirmed && IsFasteningResumeAvailable);

    private async Task ConfirmStartAsync(CancellationToken cancellationToken)
    {
        if (!IsStartReviewAllowed)
            return;
        var resume = IsFasteningResumeConfirmed ? _reviewedFasteningJob : null;
        IsFasteningResumeConfirmed = false;
        await Machine.StartAsync(cancellationToken, resume);
    }

    private async Task CheckStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(Machine.CheckStartMaterials, cancellationToken);
            RefreshFasteningResume();
            if (Machine.StartChecks[StartArea.Station2] == StartCheckState.UnfinishedCarrier)
                SelectedStartArea = StartArea.Station2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "START material check failed.");
            State.SetError(MachineAlarm.IoCommunication, exception);
        }
        finally
        {
            OnPropertyChanged(nameof(IsStartReviewAllowed));
            OnPropertyChanged(nameof(StartStation));
            OnPropertyChanged(nameof(StartMaterialState));
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (State.IsRunning || cancellationToken.IsCancellationRequested)
            return;
        IsFasteningResumeConfirmed = false;
        // Register the async command's cancellation before entering the modal message loop.
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested)
            return;
        using var registration = cancellationToken.Register(ConfirmStartCommand.Cancel);
        var wasActive = _active;
        _active = true;
        try
        {
            OnMachineStateChanged(this, new(null));
            OnRecipeChanged();
            // A ready machine still requires the operator to confirm this START.
            _windows.ShowStartConfirmation(this, cancellationToken);
        }
        finally
        {
            _active = wasActive;
            IsFasteningResumeConfirmed = false;
        }
        // The window starts production; this command owns its lifetime after the window closes.
        if (ConfirmStartCommand.ExecutionTask is { } starting)
            await starting;
    }

    public IAsyncRelayCommand StopCommand { get; }

    private async Task StopAsync()
    {
        try
        {
            IAsyncRelayCommand[] commands = [StartCommand, ConfirmStartCommand, CheckStartCommand, ChangeCarrierWorkCommand,
                SetStartBackupPlateCommand, HomeCommand];
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

    public IAsyncRelayCommand ResetCommand { get; }

    [ObservableProperty]
    public partial bool IsResetAllowed { get; private set; }

    [ObservableProperty]
    public partial string? ResetError { get; private set; }

    private async Task ResetAsync()
    {
        if (!IsResetAllowed)
            return;
        // Acknowledge even when hardware recovery is blocked; the controller owns admission.
        ResetError = null;
        _log.LogInformation("On-screen RESET requested.");
        try
        {
            await Machine.ResetAsync();
        }
        catch (Exception exception)
        {
            ResetError = UiText.Format($"RESET failed: {exception.Message}");
            _log.LogError(exception, "On-screen RESET failed.");
        }
    }

    private static AssemblyResult GetAssemblyResult(ConveyorStation station, HeatSinkSlot heatSink, bool inspection)
    {
        var assembly = station.Assemblies.FirstOrDefault(item => item.HeatSink == heatSink);
        if (assembly is null)
            return AssemblyResult.Pending;
        if (inspection)
            return assembly.InspectionResult;
        return assembly.FasteningResult;
    }

    private void OnPcbSupplyMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(AxisStatus.State))
            OnPcbSupplyChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(SupplyPositionKnown));
            OnPropertyChanged(nameof(PcbSupplyMapPosition));
        }
    }

    private void OnPcbPlacementMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(AxisStatus.State))
            OnPcbPlacementChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(PlacementPositionKnown));
            OnPropertyChanged(nameof(PcbPlacementMapPosition));
        }
    }

    private void OnBoltFasteningMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(AxisStatus.State))
            OnBoltFasteningChanged();

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(FasteningPositionKnown));
            OnPropertyChanged(nameof(ShootingHeadMapPosition));
            OnPropertyChanged(nameof(PickupHeadMapPosition));
        }
    }

    private void OnInspectionGantryMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_active)
            return;

        if (e.PropertyName is nameof(MotionStatus.IsMoving) or nameof(AxisStatus.State))
        {
            OnInspectionChanged();
            OnMainConveyorChanged();
        }

        if (e.PropertyName == nameof(MotionStatus.Position))
        {
            OnPropertyChanged(nameof(InspectionPositionKnown));
            OnPropertyChanged(nameof(InspectionGantryMapPosition));
            OnPropertyChanged(nameof(NgPickupMapPosition));
        }
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MachineState.IsRunning) or nameof(MachineState.Available)
            or nameof(MachineState.RepeatEnabled))
        {
            OnPropertyChanged(nameof(IsFasteningResumeAvailable));
            OnPropertyChanged(nameof(IsStartReviewAllowed));
        }
        if (e.PropertyName is null or nameof(MachineState.Available) or nameof(MachineState.SafetyReady)
            or nameof(MachineState.Alarm) or nameof(MachineState.FeedbackReadiness) or nameof(MachineState.IsHoming)
            or nameof(MachineState.ServoPowerOn) or nameof(MachineState.IsRunning) or nameof(MachineState.PendingStop))
            OnPropertyChanged(nameof(MachineDisplayState));
        if (e.PropertyName is null or nameof(MachineState.AutoMode) or nameof(MachineState.Available))
            OnPropertyChanged(nameof(ModeText));
        if (e.PropertyName is null or nameof(MachineState.Alarm) or nameof(MachineState.ReadError)
            or nameof(MachineState.PendingStop) or nameof(MachineState.FeedbackReadiness) or nameof(MachineState.Available))
        {
            OnPropertyChanged(nameof(HasAlarm));
            OnPropertyChanged(nameof(Alarm));
            OnPropertyChanged(nameof(AlarmAction));
        }
        if (e.PropertyName is null or nameof(MachineState.AlarmDetail) or nameof(MachineState.ReadError) or nameof(MachineState.PendingStop))
            OnPropertyChanged(nameof(AlarmDetail));
        if (e.PropertyName is null or nameof(MachineState.AlarmMessage) or nameof(MachineState.ReadError) or nameof(MachineState.PendingStop))
            OnPropertyChanged(nameof(AlarmMessage));
        if (e.PropertyName is null or nameof(MachineState.AutomaticRunning)
            or nameof(MachineState.Alarm) or nameof(MachineState.Available)
            or nameof(MachineState.PendingStop) or nameof(MachineState.FeedbackReadiness))
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
            OnPropertyChanged(nameof(IsStartReviewAllowed));
            OnPropertyChanged(nameof(StartMaterialState));
        }
    }

    private void OnConveyorOutputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IoOutputStatus.IsOn))
            return;
        if (sender == Signals.Outputs[OutputIo.MainConveyorRun])
        {
            OnMainConveyorChanged();
        }
        else
        {
            OnNgConveyorChanged();
        }
        OnInspectionChanged();
    }

    private void OnRecipeChanged()
    {
        OnPropertyChanged(nameof(PcbSupplyMapPosition));
        OnPropertyChanged(nameof(PcbPlacementMapPosition));
        OnPropertyChanged(nameof(ShootingHeadMapPosition));
        OnPropertyChanged(nameof(PickupHeadMapPosition));
        OnPropertyChanged(nameof(InspectionGantryMapPosition));
        OnPropertyChanged(nameof(NgPickupMapPosition));
        OnPcbSupplyChanged();
        OnPcbPlacementChanged();
        OnBoltFasteningChanged();
        OnInspectionChanged();
    }

    // Devices expose Changed events; notifying their property also refreshes nested XAML bindings.
    private void OnPcbSupplyChanged()
    {
        if (!_active)
            return;

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
        OnPropertyChanged(nameof(PlacementPositionKnown));
        OnPropertyChanged(nameof(PlacementStatus));
        OnPropertyChanged(nameof(Placement));
        if (SelectedStartArea == StartArea.Station1)
            OnPropertyChanged(nameof(StartStation));
        OnPropertyChanged(nameof(PcbPlacementHeatSink1Completed));
        OnPropertyChanged(nameof(PcbPlacementHeatSink2Completed));
        OnPropertyChanged(nameof(PlacementDisplayState));
    }

    private void OnMainConveyorChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(ConveyorState));
        OnPropertyChanged(nameof(ConveyorStatus));
        OnPropertyChanged(nameof(InspectionStatus));
    }

    private void OnBoltFasteningChanged()
    {
        if (IsFasteningResumeConfirmed && !IsFasteningResumeAvailable)
            IsFasteningResumeConfirmed = false;
        OnPropertyChanged(nameof(IsFasteningResumeAvailable));
        OnPropertyChanged(nameof(IsStartReviewAllowed));
        // Retain the latest completed measurement after the carrier leaves, including
        // results produced while another screen is open. Do not wait for DB persistence.
        var latest = Fastening.Station.Assemblies.SelectMany(assembly =>
                assembly.ShootingBoltResults.Select(pair =>
                    (assembly.HeatSink, Head: FasteningHead.Shooting, Id: pair.Key, Result: pair.Value))
                .Concat(assembly.PickupBoltResults.Select(pair =>
                    (assembly.HeatSink, Head: FasteningHead.Pickup, Id: pair.Key, Result: pair.Value))))
            .OrderByDescending(row => row.Result.RecordedAt)
            .FirstOrDefault();
        if (latest.Result?.RecordedAt is { } recordedAt
            && (RecentFastening?.Result.RecordedAt is not { } previous || recordedAt > previous))
        {
            RecentFastening = new(Recipes.Current.Pcb.GetBoltName(latest.Id), latest.HeatSink, latest.Head, latest.Result);
        }

        if (!_active)
            return;

        OnPropertyChanged(nameof(FasteningState));
        OnPropertyChanged(nameof(FasteningPositionKnown));
        OnPropertyChanged(nameof(Fastening));
        if (SelectedStartArea == StartArea.Station2)
            OnPropertyChanged(nameof(StartStation));
        OnPropertyChanged(nameof(BoltFasteningActiveBolt));
        OnPropertyChanged(nameof(BoltFasteningActiveOrdinal));
        OnPropertyChanged(nameof(BoltTargets));
        OnPropertyChanged(nameof(FasteningStateVisible));
        OnPropertyChanged(nameof(BoltFasteningHeatSink1Result));
        OnPropertyChanged(nameof(BoltFasteningHeatSink2Result));
        OnPropertyChanged(nameof(BoltDisplayState));
    }

    private void OnInspectionCaptured(ImageFrame frame, HeatSinkSlot pcb, Guid? boltId)
    {
        InspectionImageCaption = boltId is { } id
            ? $"{UiText.Get(pcb)} · {Recipes.Current.Pcb.GetBoltName(id)}"
            : UiText.Format($"{UiText.Get(pcb)} · Data Matrix");
        InspectionImage = InspectionPreviewViewModel.CreateBitmap(frame);
    }

    private void OnInspectionChanged()
    {
        if (!_active)
            return;

        OnPropertyChanged(nameof(Inspection));
        if (SelectedStartArea == StartArea.Station3)
            OnPropertyChanged(nameof(StartStation));

        OnPropertyChanged(nameof(InspectionState));
        OnPropertyChanged(nameof(InspectionPositionKnown));
        OnPropertyChanged(nameof(InspectionActiveBolt));
        OnPropertyChanged(nameof(InspectionActiveOrdinal));
        OnPropertyChanged(nameof(InspectionActivePcb));
        OnPropertyChanged(nameof(InspectionActiveBarcode));
        OnPropertyChanged(nameof(InspectionPcb1Barcode));
        OnPropertyChanged(nameof(InspectionPcb2Barcode));
        OnPropertyChanged(nameof(InspectionTargets));
        OnPropertyChanged(nameof(InspectionHeatSink1Result));
        OnPropertyChanged(nameof(InspectionHeatSink2Result));
        OnPropertyChanged(nameof(InspectionDisplayState));
        OnPropertyChanged(nameof(InspectionStatus));
    }

    private void OnNgConveyorChanged()
    {
        OnPropertyChanged(nameof(MachineDisplayState));
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
                || Signals.Outputs[OutputIo.MainConveyorRun].IsOn is null)
                return null;
            return Conveyor.Step as MainConveyorState?;
        }
    }

    public NgConveyorState? NgConveyorState
    {
        get
        {
            if (!State.Available
                || Signals.Outputs[OutputIo.NgConveyorRun].IsOn is null)
                return null;
            return NgConveyor.Step as NgConveyorState?;
        }
    }

    public PcbPlacementState? PlacementState
    {
        get
        {
            if (!State.Available || !Placement.Motion.IsFeedbackAvailable)
                return null;
            return Placement.Step as PcbPlacementState?;
        }
    }

    public BoltFasteningState? FasteningState
    {
        get
        {
            if (!State.Available || !Units.BoltFastening || !Fastening.Motion.IsFeedbackAvailable)
                return null;
            return Fastening.Step as BoltFasteningState?;
        }
    }

    public InspectionStationState? InspectionState
    {
        get
        {
            if (!State.Available || !Units.Inspection || !Inspection.Motion.IsFeedbackAvailable
                || Signals.Outputs[OutputIo.MainConveyorRun].IsOn is null
                || Signals.Outputs[OutputIo.NgConveyorRun].IsOn is null)
                return null;
            return Inspection.Step as InspectionStationState?;
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

    public Enum SupplyStatus
    {
        get
        {
            var display = SupplyDisplayState;
            return display is HandlerDisplayState.Working or HandlerDisplayState.Moving or HandlerDisplayState.Waiting
                ? Supply.Step ?? display
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
            if (!Units.MainConveyor)
                return HandlerDisplayState.Disabled;
            if (State.Available
                && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running)
                return !State.AutomaticRunning && !running
                    ? HandlerDisplayState.Stopped
                    : ConveyorState ?? (Enum)MachineDisplayState.Unavailable;
            return MachineDisplayState.Unavailable;
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
                case { FeedbackReadiness.Faulted: true }:
                    return MachineDisplayState.MotionFault;
                case { IsHoming: true }:
                    return MachineDisplayState.Homing;
                case { ServoPowerOn: false }:
                    return MachineDisplayState.ServoOff;
                case { FeedbackReadiness.Homed: false }:
                    return MachineDisplayState.HomeRequired;
                case { PendingStop: not null }:
                    return MachineDisplayState.Finishing;
                case { IsRunning: true }:
                    return MachineDisplayState.Running;
                default:
                    return NgConveyor.AlarmRequired || NgConveyor.IsEjectionPending
                        ? MachineDisplayState.NgEjectionRequired : MachineDisplayState.Ready;
            }
        }
    }

    public bool StartBlocked
    {
        get
        {
            return !State.IsHoming
                && Machine.StartBlock != StartBlockReason.None;
        }
    }

    public bool FasteningStateVisible
    {
        get
        {
            return Fastening.IsRunning
                && FasteningState is not null and not BoltFasteningState.Waiting;
        }
    }

    private bool InspectionStateVisible
    {
        get
        {
            return Inspection.IsRunning
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
            if (!Units.PcbSupply)
                return HandlerDisplayState.Disabled;
            if (Alarm is MachineAlarm.PcbSupply
                || Supply.Motion.Axes.Values.Any(axis => axis.State is { Alarm: true } or { Emergency: true }))
                return HandlerDisplayState.IoAlarm;
            if (!State.Available || !Supply.Motion.IsFeedbackAvailable)
                return HandlerDisplayState.PositionUnknown;
            if (Supply.Motion.IsMoving)
                return HandlerDisplayState.Moving;
            if (!State.AutomaticRunning)
                return HandlerDisplayState.Stopped;
            return Supply.Step is PcbSupplyState.WaitingForCarrier
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

    public HandlerDisplayState PlacementDisplayState
    {
        get
        {
            if (!Units.PcbPlacement)
                return HandlerDisplayState.Disabled;
            if (Alarm is MachineAlarm.PcbPlacement
                || Placement.Motion.Axes.Values.Any(axis => axis.State is { Alarm: true } or { Emergency: true }))
                return HandlerDisplayState.IoAlarm;
            if (!State.Available || !Placement.Motion.IsFeedbackAvailable)
                return HandlerDisplayState.PositionUnknown;
            if (Placement.Motion.IsMoving)
                return HandlerDisplayState.Moving;
            if (!State.AutomaticRunning)
                return HandlerDisplayState.Stopped;
            return PlacementState is PcbPlacementState.MovingToHandoff
                or PcbPlacementState.ReturningToSupply
                or PcbPlacementState.WaitingForSupplyRelease
                or PcbPlacementState.PreparingPlacement
                or PcbPlacementState.WaitingForCarrier
                or PcbPlacementState.WaitingForSupplyGrip
                or PcbPlacementState.WaitingForSupplyDeparture
                ? HandlerDisplayState.Waiting
                : HandlerDisplayState.Working;
        }
    }

    public StationDisplayState BoltDisplayState
    {
        get
        {
            if (!Units.BoltFastening)
                return StationDisplayState.Disabled;
            if (Alarm is MachineAlarm.PickupBoltFeeder
                or MachineAlarm.ShootingBoltFeeder
                or MachineAlarm.BoltFastening
                || Fastening.Motion.Axes.Values.Any(axis => axis.State is { Alarm: true } or { Emergency: true }))
                return StationDisplayState.IoAlarm;
            if (!State.Available || !Fastening.Motion.IsFeedbackAvailable)
                return StationDisplayState.PositionUnknown;
            if (Fastening.Motion.IsMoving || State.BoltTestRunning)
                return StationDisplayState.Working;
            if (!State.AutomaticRunning)
                return StationDisplayState.Stopped;
            if (!Fastening.Station.CarrierPresent)
                return StationDisplayState.WaitingForCarrier;
            if (Fastening.Station.Completed)
                return StationDisplayState.WaitingForTransfer;
            return FasteningStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

    public Enum InspectionStatus
    {
        get
        {
            var display = InspectionDisplayState;
            switch (display)
            {
                case StationDisplayState.Working when InspectionStateVisible:
                    return InspectionState ?? (Enum)MachineDisplayState.Unavailable;
                case StationDisplayState.WaitingForTransfer when Units.Inspection && Inspection.RouteToNg:
                    return InspectionStationState.WaitingForDestination;
                case StationDisplayState.WaitingForTransfer when Units.MainConveyor
                        && ConveyorState == MainConveyorState.WaitingForRearEquipment:
                    return ConveyorState ?? (Enum)MachineDisplayState.Unavailable;
                default:
                    return display;
            }
        }
    }

    public StationDisplayState InspectionDisplayState
    {
        get
        {
            if (!Units.Inspection)
                return StationDisplayState.Disabled;
            if (Alarm is MachineAlarm.Inspection or MachineAlarm.NgCarrierTransfer
                || Inspection.Motion.Axes.Values.Any(axis => axis.State is { Alarm: true } or { Emergency: true }))
                return StationDisplayState.IoAlarm;
            if (!State.Available || !Inspection.Motion.IsFeedbackAvailable)
                return StationDisplayState.PositionUnknown;
            if (!State.AutomaticRunning && !Inspection.Motion.IsMoving)
                return StationDisplayState.Stopped;
            if (Inspection.Motion.IsMoving
                || Inspection.IsTransferPending
                || InspectionState is InspectionStationState.PreparingTransfer
                    or InspectionStationState.PickingCarrier or InspectionStationState.PlacingCarrier
                    or InspectionStationState.WaitingForShuttleDown)
                return StationDisplayState.Working;
            if (!Inspection.Station.CarrierPresent)
                return StationDisplayState.WaitingForCarrier;
            if (Inspection.Station.Completed)
                return StationDisplayState.WaitingForTransfer;
            return InspectionStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

    public ObservableCollection<PcbRecord> PcbRecords { get; }

    public IAsyncRelayCommand LoadOlderPcbsCommand { get; }

    public IAsyncRelayCommand RetryPcbSaveCommand { get; }

    public PcbResultsViewModel PcbDetails { get; }

    private void OnPcbImageSaved(long number)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnPcbImageSaved(number));
            return;
        }
        if (PcbDetails.Record?.Number == number)
            _ = PcbDetails.LoadImagesCommand.ExecuteAsync(null);
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
    public partial bool HasOlderPcbs { get; private set; }

    private async Task LoadOlderPcbsAsync(CancellationToken cancellationToken)
    {
        PcbHistoryError = null;
        var before = _pcbHistoryLoaded && PcbRecords.Count > 0 ? PcbRecords[^1].Number : (long?)null;
        var directory = _pcbHistoryDirectory;
        try
        {
            var records = await Task.Run(
                () => _store.LoadPcbs(directory, before, MachineStore.PcbHistoryPageSize), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (directory != _pcbHistoryDirectory)
                return;
            _pcbHistoryLimit = Math.Max(MachineStore.PcbHistoryPageSize,
                before.HasValue ? PcbRecords.Count + records.Count : PcbRecords.Count);
            foreach (var record in records)
                UpdatePcbRecord(record);
            HasOlderPcbs = records.Count == MachineStore.PcbHistoryPageSize;
            _pcbHistoryLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (directory == _pcbHistoryDirectory)
                PcbHistoryError = UiText.Get("Cannot load PCB history. Open Logs for details.");
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
        var selected = PcbDetails.Record?.Number == record.Number;
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
            PcbDetails.Record = record;
        if (PcbRecords.Count > _pcbHistoryLimit)
        {
            PcbRecords.RemoveAt(PcbRecords.Count - 1);
            HasOlderPcbs = true;
        }
    }
}
