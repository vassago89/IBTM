using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace IBTM.Presentation.ViewModels;

public partial class ProcessZoneState(int number) : ObservableObject
{
    public int Number { get; } = number;
    public ZoneVisualState Visual { get; } = new();

    [ObservableProperty] private string _timing = string.Empty;
    [ObservableProperty] private string _progress = string.Empty;

    internal DateTime StartedAt { get; set; }
}

/// <summary>Owns all mutable state rendered by the process dashboard.</summary>
public partial class ProcessPresentationState : ObservableObject
{
    private readonly ProcessStageTracker _stages = new();
    private readonly ProcessZoneState[] _zones;
    private InspectionResult _lastRoute = InspectionResult.Unknown;
    private int _currentBoltIndex = -1;
    private bool _boltStageHasWarning;

    [ObservableProperty] private string _statusMessage = "Waiting";
    [ObservableProperty] private bool _isError;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _goodCount;
    [ObservableProperty] private int _ngCount;
    [ObservableProperty] private double _ngRate;
    [ObservableProperty] private double _lastCycleTime;
    [ObservableProperty] private string _uptime = "00:00:00";

    [ObservableProperty] private string _boltProgress = string.Empty;
    [ObservableProperty] private string _lastFiducialResult = string.Empty;

    [ObservableProperty] private int _ngStackCount;
    [ObservableProperty] private int _ngStackMaxCount;
    [ObservableProperty] private bool _ngStackAlarm;

    [ObservableProperty] private bool _isNgPath;
    [ObservableProperty] private bool _isGoodPath;
    [ObservableProperty] private string _lastRouteText = "──";
    [ObservableProperty] private ImageSource? _inspectionImage;

    [ObservableProperty] private bool _shuttleTransit12;
    [ObservableProperty] private bool _shuttleTransit23;
    [ObservableProperty] private bool _shuttleTransitOut;
    [ObservableProperty] private bool _smemaWaiting;
    [ObservableProperty] private bool _smemaReady;
    [ObservableProperty] private bool _prevEquipReady;

    public ProcessZoneState Zone1 { get; } = new(1);
    public ProcessZoneState Zone2 { get; } = new(2);
    public ProcessZoneState Zone3 { get; } = new(3);
    public ObservableCollection<NgSlotViewModel> NgStackSlots { get; } = [];

    public ProcessPresentationState(Recipe recipe, int ngStackCapacity)
    {
        _zones = [Zone1, Zone2, Zone3];
        BuildBoltMarkers(recipe);
        SetNgStackCapacity(ngStackCapacity);
        UpdateZoneLayouts(recipe);
    }

    public void ApplyPosition(ZonePositionEventArgs position) =>
        Zone(position.Zone).Visual.UpdatePosition(position.X, position.Y, position.Z);

    public void ApplyStage(StageChangedEventArgs change, Recipe recipe)
    {
        IsError = change.Status == StageStatus.Error;
        StatusMessage = GetStatusMessage(change.Stage, change.Status);
        UpdateStage(change.Stage, change.Status, recipe);
        UpdateMachineVisual(change.Stage, change.Status);
        UpdateTiming(change.Stage, change.Status);
        UpdateActivity(change.Stage, change.Status);
    }

    public void ApplyStats(ProductionStats stats, int ngStackCount, int ngStackCapacity)
    {
        TotalCount = stats.TotalCount;
        GoodCount = stats.GoodCount;
        NgCount = stats.NgCount;
        NgRate = stats.NgRate;
        LastCycleTime = stats.LastCycleTimeSeconds;
        NgStackCount = ngStackCount;
        SetNgStackCapacity(ngStackCapacity);
    }

    public void ApplyRoute(InspectionResult result)
    {
        _lastRoute = result;
        IsNgPath = result == InspectionResult.Ng;
        IsGoodPath = result == InspectionResult.Good;
        LastRouteText = result switch
        {
            InspectionResult.Good => "GOOD",
            InspectionResult.Ng => "NG",
            _ => "──",
        };

        if (result == InspectionResult.Good)
        {
            _stages.Set(ProcessStage.Zone3_NgTransfer, StageStatus.Skipped);
        }
        else if (result == InspectionResult.Ng)
        {
            _stages.Set(ProcessStage.Zone3_SmemaWait, StageStatus.Skipped);
            _stages.Set(ProcessStage.Zone3_Discharge, StageStatus.Skipped);
        }

        Zone3.Progress = _stages.Progress(3);
    }

    public void ApplyFiducial(FiducialResult result, Recipe recipe)
    {
        if (!result.Found)
        {
            LastFiducialResult = Loc.S("Fiducial_Failed");
            Zone2.Visual.FiducialOffsetVisibility = Visibility.Collapsed;
            return;
        }

        LastFiducialResult =
            $"dX:{result.OffsetX:+0.000;-0.000}  dY:{result.OffsetY:+0.000;-0.000}";
        var fiducial = recipe.Zone2_FiducialPos;
        var (canvasX, canvasY) = Zone2.Visual.ToCanvas(
            fiducial.X + result.OffsetX,
            fiducial.Y + result.OffsetY);
        Zone2.Visual.FiducialOffsetLeft = canvasX;
        Zone2.Visual.FiducialOffsetTop = canvasY;
        Zone2.Visual.FiducialCrossH1 = canvasX - 8;
        Zone2.Visual.FiducialCrossH2 = canvasX + 8;
        Zone2.Visual.FiducialCrossV1 = canvasY - 8;
        Zone2.Visual.FiducialCrossV2 = canvasY + 8;
        Zone2.Visual.FiducialOffsetVisibility = Visibility.Visible;
    }

    public void ApplyBoltProgress(BoltProgressEventArgs progress)
    {
        BoltProgress = $"{progress.BoltName}  {progress.Current}/{progress.Total}";
        _currentBoltIndex = progress.Current - 1;
        Zone2.Visual.BoltMarkers[_currentBoltIndex].State = 1;
        Zone2.Visual.ActivityLabel =
            Loc.S("Act_BoltProgress", progress.Current, progress.Total);
    }

    public void ApplyBoltResult(BoltResult result)
    {
        var marker = Zone2.Visual.BoltMarkers[_currentBoltIndex];
        marker.State = result.Success ? 2 : -1;
        marker.TorqueText = $"{result.Torque:F1}";
        _boltStageHasWarning |= !result.Success;
    }

    public void ApplyInspection(InspectionResult result)
    {
        Zone3.Visual.InspectResultText = result switch
        {
            InspectionResult.Good => "GOOD",
            InspectionResult.Ng => "NG",
            _ => string.Empty,
        };
    }

    public void UpdateNgStack(int count, bool alarm, int capacity)
    {
        NgStackCount = count;
        NgStackAlarm = alarm;
        SetNgStackCapacity(capacity);
        SyncNgSlots();
    }

    public void CaptureInspectionImage(ImageSource image) => InspectionImage = image;

    public void ApplyGripper((int Zone, bool Active) state) =>
        Zone(state.Zone).Visual.GripperActive = state.Active;

    public void PcbPlaced() =>
        Zone1.Visual.PcbCount = Math.Min(2, Zone1.Visual.PcbCount + 1);

    public void ResetNgStack() => UpdateNgStack(0, alarm: false, NgStackMaxCount);

    public void UpdateUptime(string uptime) => Uptime = uptime;

    private ProcessZoneState Zone(int number) => _zones[number - 1];

    private void BuildBoltMarkers(Recipe recipe)
    {
        Zone2.Visual.BoltMarkers.Clear();
        var pcbCenters = new[]
        {
            (X: recipe.Zone2_Pcb1CenterX, Y: recipe.Zone2_PcbCenterY, Label: "P1"),
            (X: recipe.Zone2_Pcb2CenterX, Y: recipe.Zone2_PcbCenterY, Label: "P2"),
        };

        foreach (var pcb in pcbCenters)
        {
            foreach (var bolt in recipe.BoltPoints)
            {
                Zone2.Visual.BoltMarkers.Add(new BoltMarkerViewModel(
                    $"{pcb.Label}-{bolt.Name}",
                    pcb.X + bolt.X,
                    pcb.Y + bolt.Y));
            }
        }
    }

    private void SetNgStackCapacity(int capacity)
    {
        NgStackMaxCount = capacity;
        while (NgStackSlots.Count < capacity)
        {
            NgStackSlots.Add(new NgSlotViewModel());
        }

        while (NgStackSlots.Count > capacity)
        {
            NgStackSlots.RemoveAt(NgStackSlots.Count - 1);
        }
    }

    private void SyncNgSlots()
    {
        for (var index = 0; index < NgStackSlots.Count; index++)
        {
            NgStackSlots[index].IsFilled = index < NgStackCount;
        }
    }

    private void UpdateZoneLayouts(Recipe recipe)
    {
        Zone1.Visual.UpdateZone1Layout(
            recipe.Zone1_PcbPick1.X,
            recipe.Zone1_PcbPick1.Y,
            recipe.Zone1_PcbPick2.X,
            recipe.Zone1_PcbPick2.Y,
            recipe.Zone1_PcbPlace1.X,
            recipe.Zone1_PcbPlace1.Y,
            recipe.Zone1_PcbPlace2.X,
            recipe.Zone1_PcbPlace2.Y);
        Zone3.Visual.UpdateZone3Layout(
            recipe.Zone3_NgPlacePos.X,
            recipe.Zone3_NgPlacePos.Y);
    }

    private void UpdateStage(ProcessStage stage, StageStatus status, Recipe recipe)
    {
        if (status == StageStatus.Running)
        {
            if (stage == ProcessStage.Zone1_WaitShuttle)
            {
                _stages.Reset(1);
            }
            else if (stage == ProcessStage.Zone2_WaitShuttle)
            {
                ResetZone2(recipe);
            }
            else if (stage == ProcessStage.Zone3_WaitShuttle)
            {
                ResetZone3();
            }
        }

        var effectiveStatus = status;
        if (stage == ProcessStage.Zone2_BoltTighten
            && status == StageStatus.Done
            && _boltStageHasWarning)
        {
            effectiveStatus = StageStatus.Warning;
        }
        else if (stage == ProcessStage.Zone3_Inspect
                 && status == StageStatus.Done
                 && _lastRoute == InspectionResult.Ng)
        {
            effectiveStatus = StageStatus.Warning;
        }

        _stages.Set(stage, effectiveStatus);
        var definition = ProcessStageCatalog.Find(stage);
        if (definition is not null)
        {
            Zone(definition.Zone).Progress = _stages.Progress(definition.Zone);
        }
    }

    private void ResetZone2(Recipe recipe)
    {
        _stages.Reset(2);
        Zone2.Visual.FiducialOffsetVisibility = Visibility.Collapsed;
        LastFiducialResult = string.Empty;
        _currentBoltIndex = -1;
        BoltProgress = string.Empty;
        _boltStageHasWarning = false;
        BuildBoltMarkers(recipe);
    }

    private void ResetZone3()
    {
        _stages.Reset(3);
        _lastRoute = InspectionResult.Unknown;
        IsNgPath = false;
        IsGoodPath = false;
        LastRouteText = "──";
        Zone3.Visual.InspectResultText = string.Empty;
        InspectionImage = null;
    }

    private void UpdateMachineVisual(ProcessStage stage, StageStatus status)
    {
        if (stage == ProcessStage.Zone1_WaitShuttle && status == StageStatus.Running)
        {
            PrevEquipReady = false;
        }
        else if (stage == ProcessStage.Zone1_StopAlignLift && status == StageStatus.Running)
        {
            PrevEquipReady = true;
        }

        UpdateShuttle(stage, status, Zone1, arrivalPcbCount: 0,
            onRelease: () => ShuttleTransit12 = true);
        UpdateShuttle(stage, status, Zone2, arrivalPcbCount: 2,
            onRelease: () => ShuttleTransit23 = true,
            onArrival: () => ShuttleTransit12 = false);
        UpdateShuttle(stage, status, Zone3, arrivalPcbCount: 2,
            onRelease: () => ShuttleTransitOut = true,
            onArrival: () => ShuttleTransit23 = false);

        if (stage == ProcessStage.Zone3_SmemaWait && status == StageStatus.Running)
        {
            SmemaWaiting = true;
            SmemaReady = false;
        }
        else if (stage == ProcessStage.Zone3_SmemaWait && status == StageStatus.Done)
        {
            SmemaWaiting = false;
            SmemaReady = true;
        }
        else if (stage == ProcessStage.Zone3_Discharge && status == StageStatus.Running)
        {
            SmemaReady = false;
        }

        if (stage == ProcessStage.Zone3_NgTransfer && status == StageStatus.Done)
        {
            Zone3.Visual.PcbCount = Math.Max(0, Zone3.Visual.PcbCount - 1);
        }

        if (stage == ProcessStage.Zone3_WaitShuttle && status == StageStatus.Running)
        {
            ShuttleTransitOut = false;
        }
    }

    private static void UpdateShuttle(
        ProcessStage stage,
        StageStatus status,
        ProcessZoneState zone,
        int arrivalPcbCount,
        Action onRelease,
        Action? onArrival = null)
    {
        var definition = ProcessStageCatalog.Find(stage);
        if (definition?.Zone != zone.Number)
        {
            return;
        }

        if (definition.StartsZoneTiming && status == StageStatus.Running)
        {
            zone.Visual.ShuttlePresent = true;
            zone.Visual.CarrierCount = 2;
            zone.Visual.PcbCount = arrivalPcbCount;
            onArrival?.Invoke();
        }
        else if (definition.StartsZoneTiming && status == StageStatus.Done)
        {
            zone.Visual.IsLifted = true;
        }
        else if (definition.CompletesZoneTiming && status == StageStatus.Running)
        {
            zone.Visual.IsLifted = false;
        }
        else if (definition.CompletesZoneTiming && status == StageStatus.Done)
        {
            zone.Visual.ShuttlePresent = false;
            onRelease();
        }
    }

    private void UpdateTiming(ProcessStage stage, StageStatus status)
    {
        var definition = ProcessStageCatalog.Find(stage);
        if (definition is null)
        {
            return;
        }

        var zone = Zone(definition.Zone);
        if (definition.StartsZoneTiming && status == StageStatus.Running)
        {
            zone.StartedAt = DateTime.Now;
        }
        else if (definition.CompletesZoneTiming && status == StageStatus.Done)
        {
            zone.Timing = $"{(DateTime.Now - zone.StartedAt).TotalSeconds:F1}s";
        }
    }

    private void UpdateActivity(ProcessStage stage, StageStatus status)
    {
        var definition = ProcessStageCatalog.Find(stage);
        if (definition is null)
        {
            return;
        }

        var visual = Zone(definition.Zone).Visual;
        if (status == StageStatus.Error)
        {
            visual.ActivityLabel = Loc.S("Act_Error");
        }
        else if (status == StageStatus.Done
                 && stage is ProcessStage.Zone1_Release
                     or ProcessStage.Zone2_Release
                     or ProcessStage.Zone3_Release
                     or ProcessStage.Zone3_Discharge)
        {
            visual.ActivityLabel = string.Empty;
        }
        else if (status == StageStatus.Running)
        {
            visual.ActivityLabel = Loc.S(definition.ActivityKey);
        }
    }

    private static string GetStatusMessage(ProcessStage stage, StageStatus status)
    {
        if (status == StageStatus.Error)
        {
            return Loc.S("Stat_Error", stage);
        }

        if (stage == ProcessStage.Complete)
        {
            return Loc.S("Stat_CycleComplete");
        }

        var definition = ProcessStageCatalog.Find(stage);
        return definition is null
            ? Loc.S("Stat_Waiting")
            : Loc.S(definition.StatusKey);
    }
}
