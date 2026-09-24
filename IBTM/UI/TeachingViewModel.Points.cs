using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class TeachingViewModel
{
    [ObservableProperty]
    public partial FasteningHead NewFasteningHead { get; set; } = FasteningHead.Shooting;

    [ObservableProperty]
    public partial HeatSinkSlot SelectedPcb { get; set; } = HeatSinkSlot.HeatSink1;

    partial void OnSelectedPcbChanged(HeatSinkSlot value)
    {
        RefreshTeachingPoints();
        NotifyManualTeachingCommands();
    }

    [ObservableProperty]
    public partial IReadOnlyList<TeachingPoint> FilteredPoints { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    public partial TeachingPoint? SelectedPoint { get; set; }

    partial void OnSelectedPointChanged(TeachingPoint? oldValue, TeachingPoint? newValue)
    {
        CancelTeaching();
        if (Inspection.IsLiveView || ToggleLiveViewCommand.IsRunning)
            _ = RequestCameraStopAsync();
        LiveLightLevel = (SelectedBarcode is { } pcb
            ? InspectionRecipe.GetDataMatrix(pcb).LightLevel : newValue?.Position.Bolt?.LightLevel)
            ?? InspectionRecipe.LightLevel;
        OnPropertyChanged(nameof(CameraImage));
        SelectPreviousPointCommand.NotifyCanExecuteChanged();
        SelectNextPointCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveBehavior));
        NotifyManualTeachingCommands();
        OnPropertyChanged(nameof(SelectedBarcode));
        OnPropertyChanged(nameof(IsDataMatrixSelected));
        OnPropertyChanged(nameof(IsBoltSelected));
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    public partial string? SaveError { get; set; }

    public FasteningHead[] FasteningHeads { get; }
    public HeatSinkSlot[] HeatSinkSlots { get; }

    public HeatSinkSlot? SelectedBarcode => SelectedPoint?.Position.Target == TeachingTarget.DataMatrix ? SelectedPcb : null;

    public bool IsDataMatrixSelected => SelectedBarcode is not null;

    public bool BoltPointEditorVisible => IsFasteningSelected || IsInspectionSelected;

    public bool IsBoltSelected => IsInspectionSelected && SelectedPoint?.Position.Bolt is not null;

    public TeachingSaveBehavior SaveBehavior
    {
        get
        {
            switch (SelectedPoint)
            {
                case { Position.Target: TeachingTarget.BoltPickup }:
                    return TeachingSaveBehavior.BoltPickup;
                case { Position.Target: TeachingTarget.ShootingHeadFasteningZ or TeachingTarget.PickupHeadFasteningZ }:
                    return TeachingSaveBehavior.FasteningZ;
                case { Position.Target: TeachingTarget.DataMatrix }:
                    return TeachingSaveBehavior.BarcodeFov;
                case { Position.Target: TeachingTarget.SupplyHandoff }:
                    return TeachingSaveBehavior.SupplyHandoff;
                case { Position.Target: TeachingTarget.PlacementHandoff }:
                    return TeachingSaveBehavior.PlacementHandoff;
                case { Position.Target: TeachingTarget.PlacementReceiveZ }:
                    return TeachingSaveBehavior.PlacementReceiveZ;
                case { Position.Target: TeachingTarget.NgCarrierPickup }:
                    return TeachingSaveBehavior.NgPickup;
                case { Position.Target: TeachingTarget.BoltPosition }:
                    return TeachingSaveBehavior.BoltPosition;
                case { Position.Target: TeachingTarget.CarrierUpperLeftLocatingPin or TeachingTarget.CarrierLowerRightLocatingPin }:
                    return TeachingSaveBehavior.CameraCenter;
                case { Position.Mode: TeachMode.Image }:
                    return TeachingSaveBehavior.Image;
                case { Storage: TeachingStorage.Handoff }:
                    return TeachingSaveBehavior.Handoff;
                case { Storage: TeachingStorage.Machine }:
                    return TeachingSaveBehavior.Machine;
                default:
                    return TeachingSaveBehavior.Recipe;
            }
        }
    }

    private int CurrentPointIndex
    {
        get
        {
            for (var index = 0; index < FilteredPoints.Count; index++)
                if (FilteredPoints[index] == SelectedPoint)
                    return index;
            return -1;
        }
    }

    private TeachingPoint? NextTeachingPoint
    {
        get
        {
            switch (true)
            {
                case true when !IsInspectionSelected:
                    return null;
                case true when !Inspection.HasBarcodePosition(SelectedPcb):
                    return FilteredPoints.FirstOrDefault(
                        point => point.Position.Target == TeachingTarget.DataMatrix);
                default:
                    return Recipes.Current.CarrierImages.Count == 0
                        ? null
                        : FilteredPoints.FirstOrDefault(
                            point => point.Position.Bolt is { } bolt && !Inspection.HasPosition(bolt));
            }
        }
    }

    private void RefreshTeachingPoints()
    {
        var selectedTarget = SelectedPoint?.Position.Target;
        var selectedBolt = SelectedPoint?.Position.Bolt;
        TeachingPoint Point(TeachingTarget target, TeachMode mode, BoltPoint? bolt = null)
        {
            return new(new(target, ActiveMotionGroup, mode) { Bolt = bolt }, _settings, Recipes, SelectedPcb);
        }
        TeachingPoint[] points = SelectedTeachingUnit switch
        {
            HardwareArea.PcbSupply => [
                Point(TeachingTarget.SafeZ, TeachMode.ZOnly),
                Point(TeachingTarget.SupplyPcb1Pick, TeachMode.Full),
                Point(TeachingTarget.SupplyPcb2Pick, TeachMode.Full),
                Point(TeachingTarget.SupplyHandoff, TeachMode.Full),
            ],
            HardwareArea.PcbPlacementHandler => [
                Point(TeachingTarget.PlacementHandoff, TeachMode.Full),
                Point(TeachingTarget.PlacementReceiveZ, TeachMode.ZOnly),
                Point(TeachingTarget.HeatSink1PcbPlacement, TeachMode.Full),
                Point(TeachingTarget.HeatSink2PcbPlacement, TeachMode.Full),
            ],
            HardwareArea.BoltFastening => [
                Point(TeachingTarget.SafeZ, TeachMode.ZOnly),
                Point(TeachingTarget.ShootingHeadFasteningZ, TeachMode.ZOnly),
                Point(TeachingTarget.ShootingHeadUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.ShootingHeadLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.PickupHeadFasteningZ, TeachMode.ZOnly),
                Point(TeachingTarget.PickupHeadUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.PickupHeadLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.BoltPickup, TeachMode.Full),
                .. Recipes.Current.Pcb.GetBolts(SelectedPcb).OrderBy(bolt => bolt.Head)
                    .Select(bolt => Point(TeachingTarget.BoltPosition, TeachMode.XYOnly, bolt)),
            ],
            HardwareArea.InspectionGantry => [
                Point(TeachingTarget.CarrierUpperLeftLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.CarrierLowerRightLocatingPin, TeachMode.XYOnly),
                Point(TeachingTarget.InspectionWaiting, TeachMode.XYOnly),
                Point(TeachingTarget.NgCarrierPickup, TeachMode.XYOnly),
                Point(TeachingTarget.NgShuttlePlace, TeachMode.XYOnly),
                Point(TeachingTarget.DataMatrix, TeachMode.Image),
                .. Recipes.Current.Pcb.GetBolts(SelectedPcb)
                    .Select(bolt => Point(TeachingTarget.BoltReference, TeachMode.Image, bolt)),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(SelectedTeachingUnit)),
        };
        FilteredPoints = points
            .OrderBy(point => point.Position.Target == TeachingTarget.BoltPosition ? 0 : 1)
            .ThenBy(point => point.Group)
            .ThenBy(point => point.Position.Target == TeachingTarget.PlacementHandoff ? 0 : 1)
            .ToArray();
        SelectedPoint = FilteredPoints.FirstOrDefault(
            point => selectedBolt is not null
                ? point.Position.Bolt?.Number == selectedBolt.Number
                : point.Position.Target == selectedTarget)
            ?? NextTeachingPoint
                ?? FilteredPoints.FirstOrDefault();
    }

    private void RefreshPointPositions()
    {
        foreach (var point in FilteredPoints)
            point.Refresh();
    }

    private void OnRecipeChanged()
    {
        CameraError = null;
        if (Inspection.IsLiveView || ToggleLiveViewCommand.IsRunning)
            _ = RequestCameraStopAsync();
        SelectedPoint = null;
        if (SelectedPcb == HeatSinkSlot.HeatSink1)
            RefreshTeachingPoints();
        else
            SelectedPcb = HeatSinkSlot.HeatSink1;
        OnPropertyChanged(nameof(InspectionRecipe));
        ShowRecipeImages();
    }

    public IRelayCommand AddBoltPointCommand { get; }

    private void AddBoltPoint()
    {
        var number = Recipes.Current.Pcb.GetBolts(SelectedPcb).Select(bolt => bolt.Number).DefaultIfEmpty().Max() + 1;
        var bolt = new BoltPoint
        {
            Number = number,
            HeatSink = SelectedPcb,
            Head = NewFasteningHead,
            BrightnessThreshold = InspectionRecipe.BrightnessThreshold,
            MinimumBrightRatio = InspectionRecipe.MinimumBrightRatio,
        };
        Recipes.Current.Pcb.BoltPoints.Add(bolt);
        RefreshTeachingPoints();
        SelectedPoint = FilteredPoints.First(
            point => point.BoltNumber == number && point.Position.Target == TeachingTarget.BoltReference);
    }

    private bool IsAddBoltPointAllowed => IsTeachingEditAllowed && IsInspectionSelected;

    public IRelayCommand RemoveBoltPointCommand { get; }

    private void RemoveBoltPoint()
    {
        var number = SelectedPoint!.BoltNumber;
        Recipes.Current.Pcb.BoltPoints.RemoveAll(bolt => bolt.Number == number && bolt.HeatSink == SelectedPcb);
        Recipes.Current.CarrierImages.RemoveAll(fov =>
            !fov.IsBarcode && fov.BoltNumber == number && fov.HeatSink == SelectedPcb);
        CarrierImages = CarrierImages.Where(image =>
            image.Metadata.IsBarcode || image.Metadata.BoltNumber != number || image.Metadata.HeatSink != SelectedPcb).ToArray();
        RefreshTeachingPoints();
    }

    private bool IsRemoveBoltPointAllowed
    {
        get
        {
            return IsTeachingEditAllowed
                && IsInspectionSelected
                && SelectedPoint?.Position.Target == TeachingTarget.BoltReference;
        }
    }

    public IRelayCommand SelectPreviousPointCommand { get; }

    private void SelectPreviousPoint()
    {
        SelectedPoint = FilteredPoints[CurrentPointIndex - 1];
    }

    private bool IsSelectPreviousPointAllowed => CurrentPointIndex > 0;

    public IRelayCommand SelectNextPointCommand { get; }

    private void SelectNextPoint()
    {
        SelectedPoint = FilteredPoints[CurrentPointIndex + 1];
    }

    private bool IsSelectNextPointAllowed => CurrentPointIndex < FilteredPoints.Count - 1;

    public IAsyncRelayCommand TeachCurrentPositionCommand { get; }

    private async Task TeachCurrentPositionAsync(CancellationToken cancellationToken)
    {
        if (SelectedPoint?.Position.Mode == TeachMode.Image)
        {
            if (IsRecordImagePositionAllowed)
                await CaptureTeachingImageAsync(recordPosition: true, cancellationToken);
            return;
        }
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!State.SetupEditingEnabled)
                return;
            using var operation = Machine.BeginManualOperation(
                () => State.ManualMode,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (SelectedPoint)
            {
                case { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true } point
                    when Motion.Feedback.IsReady && IsReadTeachingPositionAllowed(point, live: true):
                    SaveError = null;
                    var current = Motion.Feedback.GetPosition();
                    point.Teach(current.X, current.Y, current.Z);
                    RefreshPointPositions();
                    if (point.Storage == TeachingStorage.Machine
                        && !await SaveSettingsAsync(operation.Token, point.Setting!))
                        return;
                    operation.Token.ThrowIfCancellationRequested();
                    OnPointTaught(point);
                    NotifyManualTeachingCommands();
                    break;
                case { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true }:
                    SaveError = "Home the axes used by this teaching position and wait for them to stop before teaching.";
                    break;
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(MachineAlarm.IoCommunication, exception);
        }
    }

    private bool IsTeachCurrentPositionAllowed
    {
        get
        {
            if (SelectedPoint?.Position.Mode == TeachMode.Image)
                return IsRecordImagePositionAllowed;
            return SelectedPoint is { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true } point
                && IsTeachingEditAllowed
                && IsReadTeachingPositionAllowed(point, live: false);
        }
    }

    private bool IsReadTeachingPositionAllowed(TeachingPoint point, bool live)
    {
        MotionAxis[] axes = point.Position.Mode switch
        {
            TeachMode.Full => [MotionAxis.X, MotionAxis.Y, MotionAxis.Z],
            TeachMode.XYOnly => [MotionAxis.X, MotionAxis.Y],
            TeachMode.XOnly => [MotionAxis.X],
            TeachMode.YOnly => [MotionAxis.Y],
            TeachMode.ZOnly => [MotionAxis.Z],
            _ => [],
        };
        return axes.Length > 0 && axes.All(axis =>
            (live ? Motion.Feedback.GetAxisState(axis) : Motion.Axes[axis].State)
                is { Homed: true, InMotion: false });
    }

    private void OnPointTaught(TeachingPoint point)
    {
        if (point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin)
        {
            SelectedPoint = FilteredPoints.First(
                candidate => candidate.Position.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        }
    }

    public IAsyncRelayCommand SaveCommand { get; }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!IsSaveAllowed)
                return;
            using var operation = Machine.BeginManualOperation(
                () => State.ManualMode,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            RecipeEditor.Error = null;
            if (await SaveSettingsAsync(operation.Token,
                    _settings.PcbSupply, _settings.PcbPlacementHandler, _settings.BoltFastening,
                    _settings.InspectionGantry, _settings.CarrierReference, _settings.NgCarrierTransfer)
                && !await RecipeEditor.SaveAsync(operation.Token))
            {
                SaveError = "Teaching settings were saved, but the recipe was not saved. "
                    + (RecipeEditor.Error ?? "Save was cancelled. Save again to finish.");
            }
            NotifyManualTeachingCommands();
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(MachineAlarm.IoCommunication, exception);
        }
    }

    private bool IsSaveAllowed => IsTeachingEditAllowed && RecipeEditor.IsSaveAllowed && !RecipeEditor.IsBusy;

    private async Task<bool> SaveSettingsAsync(
        CancellationToken cancellationToken,
        params Setting[] settings)
    {
        SaveError = null;
        try
        {
            await _store.SaveSettingsAsync(settings, cancellationToken);
            _logger.LogInformation(
                "Teaching settings saved: {Group}.",
                ActiveMotionGroup);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SaveError = "Teaching save cancelled. Values have not been saved.";
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Teaching settings save failed: {Group}.", ActiveMotionGroup);
            SaveError = $"Teaching values were not saved: {exception.GetBaseException().Message}";
            return false;
        }
    }
}
