using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Sequence;
using IBTM.Stations.BoltFastening;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class ProcessViewModel : ObservableObject
{
    private readonly AutoSequence _sequence;
    private readonly ProcessEvents _events;
    private readonly IIoService _io;
    private readonly IConveyorServo _conveyor;
    private readonly MachineSettings _settings;
    private readonly MotionService _pcbSupplyMotion;
    private readonly MotionService _pcbPlacementMotion;
    private readonly MotionService _boltFasteningMotion;
    private readonly MotionService _inspectionMotion;
    private readonly IoIndicator[] _allIo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetNgStackCommand))]
    [NotifyPropertyChangedFor(nameof(ManualControlsEnabled))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private double _standardTargetTorque;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private double _loctiteTargetTorque;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyPropertyChangedFor(nameof(ManualControlsEnabled))]
    private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetNgStackCommand))]
    [NotifyPropertyChangedFor(nameof(ManualControlsEnabled))]
    private bool _ngStackAlarm;

    [ObservableProperty] private string _statusMessage = "Waiting";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _goodCount;
    [ObservableProperty] private int _ngCount;
    [ObservableProperty] private double _ngRate;
    [ObservableProperty] private double _lastCycleTime;

    [ObservableProperty] private string _pcbSupplyActivity = "Waiting";
    [ObservableProperty] private string _pcbPlacementActivity = "Waiting";
    [ObservableProperty] private string _boltFasteningActivity = "Waiting";
    [ObservableProperty] private string _inspectionActivity = "Waiting";
    [ObservableProperty] private string _pcbSupplyPosition = "X 0.000   Z 0.000";
    [ObservableProperty] private string _pcbPlacementPosition = "X 0.000   Y 0.000   Z 0.000";
    [ObservableProperty] private string _boltFasteningPosition = "X 0.000   Y 0.000   Z 0.000";
    [ObservableProperty] private string _inspectionPosition = "X 0.000   Y 0.000   Z 0.000";
    [ObservableProperty] private bool _pcbSupplyMoving;
    [ObservableProperty] private bool _pcbPlacementMoving;
    [ObservableProperty] private bool _boltFasteningMoving;
    [ObservableProperty] private bool _inspectionMoving;
    [ObservableProperty] private bool _conveyorRunning;
    [ObservableProperty] private bool _conveyorReady;
    [ObservableProperty] private string _boltFasteningLocation = "PCB 1";
    [ObservableProperty] private string _inspectionLocation = "PCB 1";
    [ObservableProperty] private int _boltFasteningMapColumn;
    [ObservableProperty] private int _inspectionMapColumn;

    [ObservableProperty] private double _pcbSupplyMapLeft = 282;
    [ObservableProperty] private double _pcbSupplyZDepth = 6;
    [ObservableProperty] private double _pcbPlacementMapLeft = 280;
    [ObservableProperty] private double _pcbPlacementMapTop = 108;
    [ObservableProperty] private double _pcbPlacementZDepth = 6;
    [ObservableProperty] private double _supplyCarrier1MapLeft = 120;
    [ObservableProperty] private double _supplyCarrier2MapLeft = 450;
    [ObservableProperty] private double _supplyRotationMapLeft = 200;
    [ObservableProperty] private double _housing1MapLeft = 180;
    [ObservableProperty] private double _housing2MapLeft = 400;
    [ObservableProperty] private bool _pcbSupplyPcbDetected;
    [ObservableProperty] private bool _pcbPlacementPcbDetected;
    [ObservableProperty] private bool _pcbSupplyGripperClosed;
    [ObservableProperty] private bool _pcbPlacementGripperClosed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PcbSupplyRotationAngle))]
    private bool _pcbSupplyRotated;
    [ObservableProperty] private bool _pcbSupplyCarrierAvailable;
    [ObservableProperty] private bool _pcbPlacementCarrierJigPresent;
    [ObservableProperty] private bool _pcbPlacementHousing1Present;
    [ObservableProperty] private bool _pcbPlacementHousing2Present;
    [ObservableProperty] private bool _alignmentCameraActive;

    [ObservableProperty] private string _boltProgress = string.Empty;
    [ObservableProperty] private string _lastBoltResult = string.Empty;

    [ObservableProperty] private int _ngStackCount;
    [ObservableProperty] private int _ngStackMaxCount;
    [ObservableProperty] private bool _isNgPath;
    [ObservableProperty] private bool _isGoodPath;
    [ObservableProperty] private string _lastRouteText = "—";
    [ObservableProperty] private ImageSource? _inspectionImage;
    [ObservableProperty] private string _pcb1InspectionResult = "—";
    [ObservableProperty] private string _pcb2InspectionResult = "—";
    [ObservableProperty] private bool _smemaWaiting;
    [ObservableProperty] private bool _smemaReady;

    public ProcessViewModel(
        AutoSequence sequence,
        ProcessEvents events,
        MachineSettings settings,
        IIoService io,
        IConveyorServo conveyor,
        HardwareMap hardware,
        [FromKeyedServices(EquipmentUnit.PcbSupply)] MotionService pcbSupplyMotion,
        [FromKeyedServices(EquipmentUnit.PcbPlacement)] MotionService pcbPlacementMotion,
        [FromKeyedServices(EquipmentUnit.BoltFastening)] MotionService boltFasteningMotion,
        [FromKeyedServices(EquipmentUnit.Inspection)] MotionService inspectionMotion)
    {
        _sequence = sequence;
        _events = events;
        _io = io;
        _conveyor = conveyor;
        _settings = settings;
        _pcbSupplyMotion = pcbSupplyMotion;
        _pcbPlacementMotion = pcbPlacementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionMotion = inspectionMotion;
        StandardTargetTorque = settings.BoltFastening.StandardHead.DefaultTorqueNm;
        LoctiteTargetTorque = settings.BoltFastening.LoctiteHead.DefaultTorqueNm;
        NgStackMaxCount = sequence.NgStackCapacity;
        BoltFasteningIo =
        [
            Input("Carrier jig", InputIo.BoltFasteningCarrierJigPresent),
            Input("Housing 1", InputIo.BoltFasteningHousing1Present),
            Input("Housing 2", InputIo.BoltFasteningHousing2Present),
            Input("Stopper up", InputIo.BoltFasteningStopperUp),
            Output("Stopper up", OutputIo.BoltFasteningStopperUp),
            Input("Plate up", InputIo.BoltFasteningBackupPlateUp),
            Output("Plate up", OutputIo.BoltFasteningBackupPlateUp),
            Output("Laser", OutputIo.BoltFasteningLaser),
        ];
        InspectionIo =
        [
            Input("Carrier jig", InputIo.InspectionCarrierJigPresent),
            Input("Housing 1", InputIo.InspectionHousing1Present),
            Input("Housing 2", InputIo.InspectionHousing2Present),
            Input("Stopper up", InputIo.InspectionStopperUp),
            Output("Stopper up", OutputIo.InspectionStopperUp),
            Input("Plate up", InputIo.InspectionBackupPlateUp),
            Output("Plate up", OutputIo.InspectionBackupPlateUp),
            Input("Gripper", InputIo.InspectionGripper),
            Output("Gripper", OutputIo.InspectionGripper),
            Output("Laser", OutputIo.InspectionLaser),
        ];
        UpstreamIo =
        [
            Input("Board available", InputIo.MainLaneUpstreamBoardAvailable),
            Output("Machine ready", OutputIo.MainLaneUpstreamMachineReady),
        ];
        DownstreamIo =
        [
            Input("Machine ready", InputIo.MainLaneDownstreamMachineReady),
            Output("Board available", OutputIo.MainLaneDownstreamBoardAvailable),
        ];
        SafetyIo =
        [
            Input("E-stop released", InputIo.EmergencyStopReleased),
            Input("Door closed", InputIo.DoorClosed),
            Input("Air pressure", InputIo.AirPressureOk),
        ];
        SystemIo =
        [
            Input("Reset", InputIo.ResetButton),
            Output("Green", OutputIo.TowerLampGreen),
            Output("Yellow", OutputIo.TowerLampYellow),
            Output("Red", OutputIo.TowerLampRed),
            Output("Buzzer", OutputIo.Buzzer),
        ];
        _allIo = BoltFasteningIo
            .Concat(InspectionIo)
            .Concat(UpstreamIo)
            .Concat(DownstreamIo)
            .Concat(SafetyIo)
            .Concat(SystemIo)
            .ToArray();
        _events.StageChanged += OnStageChanged;
        _events.StageFailed += OnStageFailed;
        _events.StatsUpdated += OnStatsUpdated;
        _events.BoltCompleted += OnBoltCompleted;
        _events.BoltProgress += OnBoltProgress;
        _events.InspectionCompleted += OnInspectionCompleted;
        _events.NgStackChanged += OnNgStackChanged;
        pcbSupplyMotion.PositionChanged += OnPcbSupplyPositionChanged;
        pcbPlacementMotion.PositionChanged += OnPcbPlacementPositionChanged;
        boltFasteningMotion.PositionChanged += OnBoltFasteningPositionChanged;
        inspectionMotion.PositionChanged += OnInspectionPositionChanged;
        conveyor.RunningChanged += HandleConveyorRunningChanged;

        IoIndicator Input(string name, InputIo input) =>
            new(name, input, hardware.Inputs[input]);

        IoIndicator Output(string name, OutputIo output) =>
            new(name, output, hardware.Outputs[output]);
    }

    public bool ManualControlsEnabled => !IsRunning && !IsError && !NgStackAlarm;
    public double PcbSupplyRotationAngle => PcbSupplyRotated ? 90 : 0;
    public IoIndicator[] BoltFasteningIo { get; }
    public IoIndicator[] InspectionIo { get; }
    public IoIndicator[] UpstreamIo { get; }
    public IoIndicator[] DownstreamIo { get; }
    public IoIndicator[] SafetyIo { get; }
    public IoIndicator[] SystemIo { get; }

    public void RefreshEquipmentState()
    {
        RefreshIo();

        var conveyor = _conveyor.GetAxisState();
        ConveyorRunning = !conveyor.InPosition;
        ConveyorReady =
            conveyor.ServoOn
            && !conveyor.Alarm
            && !conveyor.Emergency;

        var pcbSupply = _pcbSupplyMotion.GetPosition();
        var pcbPlacement = _pcbPlacementMotion.GetPosition();
        var boltFastening = _boltFasteningMotion.GetPosition();
        var inspection = _inspectionMotion.GetPosition();
        PcbSupplyPosition = FormatXzPosition(pcbSupply.X, pcbSupply.Z);
        PcbPlacementPosition = FormatPosition(
            pcbPlacement.X,
            pcbPlacement.Y,
            pcbPlacement.Z);
        BoltFasteningPosition = FormatPosition(
            boltFastening.X,
            boltFastening.Y,
            boltFastening.Z);
        InspectionPosition = FormatPosition(
            inspection.X,
            inspection.Y,
            inspection.Z);
        PcbSupplyMoving = IsMoving(_pcbSupplyMotion);
        PcbPlacementMoving = IsMoving(_pcbPlacementMotion);
        BoltFasteningMoving = IsMoving(_boltFasteningMotion);
        InspectionMoving = IsMoving(_inspectionMotion);
        UpdateSupplyMap(pcbSupply);
        UpdatePlacementMap(pcbPlacement);
        UpdateBoltLocation(boltFastening);
        UpdateInspectionLocation(inspection);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsRunning = true;
        try
        {
            foreach (var bolt in _sequence.CurrentRecipe.BoltFastening.BoltPoints)
            {
                bolt.TargetTorqueNm = bolt.BoltType == BoltType.Standard
                    ? StandardTargetTorque
                    : LoctiteTargetTorque;
            }

            await _sequence.StartAsync();
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanStart() =>
        !IsRunning
        && !IsError
        && !NgStackAlarm
        && StandardTargetTorque > 0
        && LoctiteTargetTorque > 0;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _sequence.Stop();

    private bool CanStop() => IsRunning;

    [RelayCommand]
    private void EStop() => _sequence.EmergencyStop();

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => _sequence.Reset();

    private bool CanReset() => !IsRunning && IsError;

    [RelayCommand(CanExecute = nameof(CanResetNgStack))]
    private void ResetNgStack() => _sequence.ResetNgStack();

    private bool CanResetNgStack() => !IsRunning && NgStackAlarm;

    private void OnStageChanged(ProcessStage stage, StageStatus status) =>
        RunOnUi(() =>
        {
            RefreshIo();
            ApplyStage(stage, status);
        });

    private void OnStageFailed(ProcessStage stage, Exception exception) =>
        RunOnUi(() => StatusMessage = $"Alarm [{stage}] {exception.Message}");

    private void ApplyStage(ProcessStage stage, StageStatus status)
    {
        if (stage == ProcessStage.AlignPcb)
        {
            AlignmentCameraActive = status == StageStatus.Running;
        }

        if (status == StageStatus.Error)
        {
            IsError = true;
        }
        else if (stage == ProcessStage.Idle && status == StageStatus.Idle)
        {
            IsError = false;
        }

        if (!IsError || status == StageStatus.Error || stage == ProcessStage.Idle)
        {
            StatusMessage = GetStatusMessage(stage, status);
        }

        if (stage is ProcessStage.Idle or ProcessStage.Complete or ProcessStage.Error)
        {
            return;
        }

        var definition = GetStage(stage);
        SetActivity(definition.Unit, status switch
        {
            StageStatus.Running => definition.Activity,
            StageStatus.Done => "Complete",
            StageStatus.Error => "Error",
            _ => "Stopped",
        });

        if (stage == ProcessStage.SendCarrierJig)
        {
            SmemaWaiting = status == StageStatus.Running;
            SmemaReady = status == StageStatus.Done;
        }
    }

    private void OnStatsUpdated(ProductionStats stats) =>
        RunOnUi(() =>
        {
            TotalCount = stats.TotalCount;
            GoodCount = stats.GoodCount;
            NgCount = stats.NgCount;
            NgRate = stats.NgRate;
            LastCycleTime = stats.LastCycleTimeSeconds;
            NgStackCount = _sequence.NgStackCount;
            NgStackMaxCount = _sequence.NgStackCapacity;
        });

    private void OnBoltCompleted(BoltResult result) =>
        RunOnUi(() => LastBoltResult =
            $"{(result.Success ? "PASS" : "FAIL")}   {result.Torque:F1} Nm");

    private void OnBoltProgress(
        int current,
        int total,
        PcbSlot pcb,
        int boltNumber) =>
        RunOnUi(() => BoltProgress = $"{pcb}-B{boltNumber}   {current}/{total}");

    private void OnInspectionCompleted(CarrierInspectionResult result) =>
        RunOnUi(() =>
        {
            var displayed = result.Pcb1.Result == InspectionResult.Ng
                ? result.Pcb1
                : result.Pcb2.Result == InspectionResult.Ng
                    ? result.Pcb2
                    : result.Pcb1.Image is not null
                        ? result.Pcb1
                        : result.Pcb2;
            InspectionImage = displayed.Image?.ToImageSource();
            Pcb1InspectionResult = $"PCB 1  {result.Pcb1.Result.ToString().ToUpperInvariant()}";
            Pcb2InspectionResult = $"PCB 2  {result.Pcb2.Result.ToString().ToUpperInvariant()}";
            IsGoodPath = result.OverallResult == InspectionResult.Good;
            IsNgPath = !IsGoodPath;
            LastRouteText = IsGoodPath ? "GOOD" : "NG";
        });

    private void OnNgStackChanged(int count, bool alarm) =>
        RunOnUi(() =>
        {
            NgStackCount = count;
            NgStackMaxCount = _sequence.NgStackCapacity;
            NgStackAlarm = alarm;
        });

    private void SetActivity(EquipmentUnit unit, string activity)
    {
        switch (unit)
        {
            case EquipmentUnit.PcbSupply:
                PcbSupplyActivity = activity;
                break;
            case EquipmentUnit.PcbPlacement:
                PcbPlacementActivity = activity;
                break;
            case EquipmentUnit.BoltFastening:
                BoltFasteningActivity = activity;
                break;
            case EquipmentUnit.Inspection:
                InspectionActivity = activity;
                break;
        }
    }

    private static string GetStatusMessage(ProcessStage stage, StageStatus status)
    {
        if (status == StageStatus.Error)
        {
            return stage == ProcessStage.Idle
                ? "Emergency stop active"
                : $"Error [{stage}]";
        }

        if (status == StageStatus.Idle && stage != ProcessStage.Idle)
        {
            return "Stopped";
        }

        return stage switch
        {
            ProcessStage.Idle => "Waiting",
            ProcessStage.Complete => "Cycle complete",
            _ => GetStage(stage).Activity,
        };
    }

    private static (
        EquipmentUnit Unit,
        string Activity) GetStage(ProcessStage stage) =>
        stage switch
        {
            ProcessStage.SupplyPcb =>
                (EquipmentUnit.PcbSupply, "Supplying PCB"),
            ProcessStage.ReceiveCarrierJig =>
                (EquipmentUnit.PcbPlacement, "Receiving carrier jig"),
            ProcessStage.PositionPcbPlacementCarrierJig =>
                (EquipmentUnit.PcbPlacement, "Positioning carrier jig"),
            ProcessStage.AlignPcb =>
                (EquipmentUnit.PcbPlacement, "Aligning PCB"),
            ProcessStage.PlacePcb =>
                (EquipmentUnit.PcbPlacement, "Placing PCB into housing"),
            ProcessStage.TransferToBoltFastening =>
                (EquipmentUnit.PcbPlacement, "Sending carrier jig"),
            ProcessStage.PositionBoltFasteningCarrierJig =>
                (EquipmentUnit.BoltFastening, "Positioning carrier jig"),
            ProcessStage.TightenBolts =>
                (EquipmentUnit.BoltFastening, "Tightening bolts"),
            ProcessStage.TransferToInspection =>
                (EquipmentUnit.BoltFastening, "Sending carrier jig"),
            ProcessStage.PositionInspectionCarrierJig =>
                (EquipmentUnit.Inspection, "Positioning carrier jig"),
            ProcessStage.Inspect =>
                (EquipmentUnit.Inspection, "Inspecting"),
            ProcessStage.StackNgCarrierJig =>
                (EquipmentUnit.Inspection, "Stacking NG carrier jig"),
            ProcessStage.SendCarrierJig =>
                (EquipmentUnit.Inspection, "Sending carrier jig"),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

    private static string FormatPosition(double x, double y, double z) =>
        $"X {x:F3}   Y {y:F3}   Z {z:F3}";

    private static string FormatXzPosition(double x, double z) =>
        $"X {x:F3}   Z {z:F3}";

    private void OnPcbSupplyPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            var position = (X: x, Y: y, Z: z);
            PcbSupplyPosition = FormatXzPosition(x, z);
            PcbSupplyMoving = IsMoving(_pcbSupplyMotion);
            UpdateSupplyMap(position);
            RefreshHandlingIo();
        });

    private void OnPcbPlacementPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            var position = (X: x, Y: y, Z: z);
            PcbPlacementPosition = FormatPosition(x, y, z);
            PcbPlacementMoving = IsMoving(_pcbPlacementMotion);
            UpdatePlacementMap(position);
            RefreshHandlingIo();
        });

    private void OnBoltFasteningPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            BoltFasteningPosition = FormatPosition(x, y, z);
            BoltFasteningMoving = IsMoving(_boltFasteningMotion);
            UpdateBoltLocation((x, y, z));
        });

    private void OnInspectionPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            InspectionPosition = FormatPosition(x, y, z);
            InspectionMoving = IsMoving(_inspectionMotion);
            UpdateInspectionLocation((x, y, z));
        });

    private void HandleConveyorRunningChanged(bool running) =>
        RunOnUi(() => ConveyorRunning = running);

    private void RefreshIo()
    {
        foreach (var indicator in _allIo)
        {
            indicator.Refresh(_io);
        }

        RefreshHandlingIo();
    }

    private void RefreshHandlingIo()
    {
        PcbSupplyPcbDetected =
            _io.GetInput(InputIo.PcbSupplyPcbPresent);
        PcbPlacementPcbDetected =
            _io.GetInput(InputIo.PcbPlacementPcbPresent);
        PcbSupplyGripperClosed =
            _io.GetInput(InputIo.PcbSupplyGripperClosed);
        PcbPlacementGripperClosed =
            _io.GetInput(InputIo.PcbPlacementGripperClosed);
        PcbSupplyRotated =
            _io.GetInput(InputIo.PcbSupplyRotationHandoff);
        PcbSupplyCarrierAvailable =
            _io.GetInput(InputIo.PcbSupplyCarrierAvailable);
        PcbPlacementCarrierJigPresent =
            _io.GetInput(InputIo.PcbPlacementCarrierJigPresent);
        PcbPlacementHousing1Present =
            _io.GetInput(InputIo.PcbPlacementHousing1Present);
        PcbPlacementHousing2Present =
            _io.GetInput(InputIo.PcbPlacementHousing2Present);
    }

    private void UpdateSupplyMap((double X, double Y, double Z) position)
    {
        var recipe = _sequence.CurrentRecipe.PcbSupply;
        var handoffX = _settings.PcbSupply.HandoffPosition.X;
        var range = MaxDistance(
            handoffX,
            recipe.CarrierPick1.X,
            recipe.CarrierPick2.X,
            _settings.PcbSupply.RotationX);
        var scale = 220 / range;

        double MapX(double x) =>
            Math.Clamp(310 - ((x - handoffX) * scale), 48, 572);

        PcbSupplyMapLeft = MapX(position.X) - 28;
        SupplyCarrier1MapLeft = MapX(recipe.CarrierPick1.X) - 22;
        SupplyCarrier2MapLeft = MapX(recipe.CarrierPick2.X) - 22;
        SupplyRotationMapLeft =
            MapX(_settings.PcbSupply.RotationX) - 20;
        PcbSupplyZDepth = MapDepth(
            position.Z,
            _settings.PcbSupply.Motion.SafeZ,
            recipe.CarrierPick1.Z,
            recipe.CarrierPick2.Z,
            _settings.PcbSupply.HandoffPosition.Z);
    }

    private void UpdatePlacementMap(
        (double X, double Y, double Z) position)
    {
        var recipe = _sequence.CurrentRecipe.PcbPlacement;
        var handoff = _settings.PcbPlacement.HandoffPickPosition;
        var rangeX = MaxDistance(
            handoff.X,
            recipe.Fiducial1Position.X,
            recipe.Fiducial2Position.X,
            recipe.Pcb1PlacePosition.X,
            recipe.Pcb2PlacePosition.X);
        var rangeY = MaxDistance(
            handoff.Y,
            recipe.Fiducial1Position.Y,
            recipe.Fiducial2Position.Y,
            recipe.Pcb1PlacePosition.Y,
            recipe.Pcb2PlacePosition.Y);
        var scaleX = 220 / rangeX;
        var scaleY = 180 / rangeY;

        double MapX(double x) =>
            Math.Clamp(310 - ((x - handoff.X) * scaleX), 60, 560);
        double MapY(double y) =>
            Math.Clamp(135 + ((y - handoff.Y) * scaleY), 120, 322);

        var centerX = MapX(position.X);
        var centerY = MapY(position.Y);
        PcbPlacementMapLeft = centerX - 30;
        PcbPlacementMapTop = centerY - 26;

        Housing1MapLeft = MapX(recipe.Pcb1PlacePosition.X) - 34;
        Housing2MapLeft = MapX(recipe.Pcb2PlacePosition.X) - 34;
        PcbPlacementZDepth = MapDepth(
            position.Z,
            _settings.PcbPlacement.Motion.SafeZ,
            handoff.Z,
            recipe.Fiducial1Position.Z,
            recipe.Fiducial2Position.Z,
            recipe.Pcb1PlacePosition.Z,
            recipe.Pcb2PlacePosition.Z);
    }

    private static double MapDepth(
        double z,
        double safeZ,
        params double[] targets)
    {
        var travel = Math.Max(targets.Max() - safeZ, 1);
        var depth = Math.Clamp((z - safeZ) / travel, 0, 1);
        return 6 + (depth * 24);
    }

    private static double MaxDistance(
        double origin,
        params double[] positions) =>
        Math.Max(positions.Max(position => Math.Abs(position - origin)), 1);

    private void UpdateBoltLocation((double X, double Y, double Z) position)
    {
        var target = FindNearest(
            position,
            [
                (_sequence.CurrentRecipe.BoltFastening.Pcb1Reference, "PCB 1", 0),
                (_sequence.CurrentRecipe.BoltFastening.Pcb2Reference, "PCB 2", 2),
            ]);
        BoltFasteningLocation = FormatLocation(target.Name, BoltFasteningMoving);
        BoltFasteningMapColumn = target.Column;
    }

    private void UpdateInspectionLocation(
        (double X, double Y, double Z) position)
    {
        var recipe = _sequence.CurrentRecipe.Inspection;
        var target = FindNearest(
            position,
            [
                (recipe.Pcb1InspectionPosition, "PCB 1 · CAMERA", 0),
                (recipe.Pcb2InspectionPosition, "PCB 2 · CAMERA", 1),
                (recipe.NgCarrierPickupPosition, "NG PICKUP", 1),
                (recipe.NgStackPosition, "NG STACK", 2),
            ]);
        InspectionLocation = FormatLocation(
            target.Name,
            InspectionMoving);
        InspectionMapColumn = target.Column;
    }

    private static (string Name, int Column) FindNearest(
        (double X, double Y, double Z) position,
        params (AxisPos Position, string Name, int Column)[] targets)
    {
        var nearest = targets.MinBy(target =>
            ((position.X - target.Position.X) * (position.X - target.Position.X))
            + ((position.Y - target.Position.Y) * (position.Y - target.Position.Y)));
        return (nearest.Name, nearest.Column);
    }

    private static string FormatLocation(string target, bool moving) =>
        moving ? $"MOVING · {target}" : target;

    private static bool IsMoving(MotionService motion) =>
        motion.Axes.Any(axis => !motion.GetAxisState(axis).InPosition);

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);
}
