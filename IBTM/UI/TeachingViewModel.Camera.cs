using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class TeachingViewModel
{
    private readonly object _liveImageGate;
    private ImageFrame? _pendingLiveFrame;
    private bool _liveImageUpdateQueued;
    private Task _liveImageUpdate = Task.CompletedTask;
    private Task _cameraStop = Task.CompletedTask;

    [ObservableProperty]
    public partial int LiveLightLevel { get; set; }

    partial void OnLiveLightLevelChanging(int value)
    {
        if (value is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255.");
    }

    public BitmapSource? CameraImage => Inspection.IsLiveView ? LiveImage : CarrierImages.FirstOrDefault(tile =>
        tile.Metadata.HeatSink == SelectedPcb
        && (IsDataMatrixSelected ? tile.Metadata.IsBarcode
            : IsBoltSelected && !tile.Metadata.IsBarcode && tile.Metadata.BoltNumber == SelectedPoint!.BoltNumber))?.Image;

    public IAsyncRelayCommand GrabCommand { get; }

    private async Task GrabAsync(CancellationToken cancellationToken)
    {
        if (IsGrabAllowed)
            await CaptureTeachingImageAsync(recordPosition: false, cancellationToken);
    }

    private bool IsGrabAllowed => IsRecordImagePositionAllowed && SelectedPoint?.Position.HasPosition == true;

    public IAsyncRelayCommand ApplyLightCommand { get; }

    private async Task ApplyLightAsync(CancellationToken cancellationToken)
    {
        if (!IsApplyLightAllowed)
            return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ViewCancellation);
        try
        {
            CameraError = null;
            await Inspection.ApplyLiveLightAsync(LiveLightLevel, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Teaching light adjustment failed.");
            CameraError = exception.Message;
        }
    }

    private bool IsApplyLightAllowed => IsInspectionSelected && IsTeachingEditAllowed && Inspection.IsLiveView;

    public IAsyncRelayCommand ToggleLiveViewCommand { get; }

    private async Task ToggleLiveViewAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ViewCancellation);
        try
        {
            if (Inspection.IsLiveView)
            {
                await StopCameraLiveAsync();
                return;
            }

            CameraError = null;
            await _cameraStop;
            cancellation.Token.ThrowIfCancellationRequested();
            await Inspection.StartLiveViewAsync(cancellation.Token, LiveLightLevel);
            if (!State.ManualMode || !IsInspectionSelected)
                await StopCameraLiveAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Camera live view operation failed.");
            CameraError = exception.Message;
        }
    }

    private bool IsToggleLiveViewAllowed => Inspection.IsLiveView
        || IsInspectionSelected && State.ManualMode && !TeachCurrentPositionCommand.IsRunning && !GrabCommand.IsRunning;

    private void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning))
            return;
        OnPropertyChanged(nameof(IsBusy));
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        GrabCommand.NotifyCanExecuteChanged();
        ApplyLightCommand.NotifyCanExecuteChanged();
    }

    private async Task CaptureTeachingImageAsync(bool recordPosition, CancellationToken cancellationToken)
    {
        var point = SelectedPoint;
        var bolt = point?.Position.Bolt;
        var barcode = SelectedBarcode is not null;
        if (bolt is null && !barcode)
            return;
        var pcb = SelectedPcb;
        var lightLevel = LiveLightLevel;
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        _logger.LogInformation(
            "Teaching capture requested: recipe={Recipe}, PCB={Pcb}, point={Point}, recordPosition={RecordPosition}, live={Live}.",
            RecipeEditor.Name, pcb, point!.Name, recordPosition, Inspection.IsLiveView);
        try
        {
            CameraError = null;
            if (State.IsRunningFor())
                throw new InvalidOperationException("Recording was not started because the machine is busy. Wait for motion to stop, then record again.");
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                throw new InvalidOperationException("Recording was not started because another operation is active. Try again after it finishes.");
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await _recipeImageUpdate;
            operation.Token.ThrowIfCancellationRequested();
            if (CarrierImages.Count != Recipes.Current.CarrierImages.Count)
                throw new InvalidOperationException("Wait for the saved teaching images to load before capturing.");
            await _cameraStop;
            operation.Token.ThrowIfCancellationRequested();
            var captured = await Inspection.CaptureCarrierImageAsync(operation.Token, lightLevel);
            _logger.LogInformation("Teaching image captured: PCB={Pcb}, point={Point}, X={X}, Y={Y}.",
                pcb, point.Name, captured.Center.X, captured.Center.Y);
            var image = await Task.Run(() => InspectionPreview.CreateBitmap(captured.Frame), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var images = CarrierImages.ToList();
            var index = images.FindIndex(tile => tile.Metadata.HeatSink == pcb
                && (barcode ? tile.Metadata.IsBarcode : !tile.Metadata.IsBarcode && tile.Metadata.BoltNumber == bolt!.Number));
            var previous = index >= 0 ? images[index].Metadata : null;
            if (!recordPosition && previous is null)
                throw new InvalidOperationException("Record Position first, then use Grab to update its reference image.");
            var metadata = new CarrierImageTile
            {
                Number = previous?.Number ?? (images.Count == 0 ? 1 : images.Max(tile => tile.Metadata.Number) + 1),
                Center = barcode ? (recordPosition ? captured.Center : previous!.Center) : null,
                HeatSink = pcb,
                BoltNumber = bolt?.Number,
                IsBarcode = barcode,
                Region = previous?.Region ?? PixelRegion.CenteredSquare(
                    image.PixelWidth, image.PixelHeight, Math.Min(image.PixelWidth, image.PixelHeight) / 4),
            };
            var replacement = new CarrierImageTileView(metadata, image, bolt);
            if (index >= 0)
                images[index] = replacement;
            else
                images.Add(replacement);

            var previousX = bolt?.X;
            var previousY = bolt?.Y;
            var previousFasteningX = bolt?.FasteningX;
            var previousFasteningY = bolt?.FasteningY;
            var dataMatrix = barcode ? InspectionRecipe.GetDataMatrix(pcb) : null;
            var previousLight = dataMatrix is not null ? dataMatrix.LightLevel : bolt!.LightLevel;
            if (dataMatrix is not null)
                dataMatrix.LightLevel = lightLevel;
            else
                bolt!.LightLevel = lightLevel;
            if (recordPosition && bolt is not null)
            {
                // The bolt is centered on the camera crosshair. ROI pixels do not alter its machine XY.
                bolt.X = captured.Center.X;
                bolt.Y = captured.Center.Y;
                _settings.BoltFastening.InitializeBoltPosition(bolt, _settings.CarrierReference);
            }
            if (await RecipeEditor.SaveCarrierImagesAsync(images, operation.Token))
            {
                CarrierImages = images;
                RefreshPointPositions();
                var position = Recipes.Current.GetInspectionPosition(metadata);
                _logger.LogInformation(
                    "Teaching image saved: recipe={Recipe}, PCB={Pcb}, point={Point}, X={X}, Y={Y}, image={Image}, database={Database}.",
                    RecipeEditor.ActiveName, pcb, point.Name, position.X, position.Y,
                    metadata.Number, _store.DatabaseFile);
            }
            else
            {
                if (dataMatrix is not null)
                    dataMatrix.LightLevel = previousLight;
                else
                    bolt!.LightLevel = previousLight;
                if (recordPosition && bolt is not null)
                {
                    bolt.X = previousX;
                    bolt.Y = previousY;
                    bolt.FasteningX = previousFasteningX;
                    bolt.FasteningY = previousFasteningY;
                }
                CameraError = RecipeEditor.Error ?? "Recording was cancelled before saving. Record the point again.";
                _logger.LogInformation("Teaching image was not saved: PCB={Pcb}, point={Point}, reason={Reason}.",
                    pcb, point.Name, CameraError);
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
            _logger.LogInformation("Teaching capture cancelled before saving: PCB={Pcb}, point={Point}.", pcb, point.Name);
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            CameraError = exception.GetBaseException().Message;
            _logger.LogError(exception, "Teaching capture failed: PCB={Pcb}, point={Point}.", pcb, point.Name);
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Teaching capture failed: PCB={Pcb}, point={Point}.", pcb, point.Name);
            if (!activeToken.IsCancellationRequested)
                CameraError = exception.Message;
        }
    }

    private bool IsRecordImagePositionAllowed
    {
        get
        {
            return IsInspectionSelected
                && (IsBoltSelected || IsDataMatrixSelected)
                && !State.IsRunning
                && Machine.IsManualMotionReady(ActiveMotionGroup, live: false)
                && Motion.Axes.Values.All(axis => axis.State is { InMotion: false, InPosition: true })
                && RecipeEditor.IsSaveAllowed;
        }
    }

    private Task StopCameraLiveAsync()
    {
        ToggleLiveViewCommand.Cancel();
        if (!_cameraStop.IsCompleted)
            return _cameraStop;

        lock (_liveImageGate)
        {
            LiveImage = null;
            _pendingLiveFrame = null;
        }

        _cameraStop = Inspection.StopLiveViewAsync();
        return _cameraStop;
    }

    private async Task RequestCameraStopAsync()
    {
        try
        {
            await StopCameraLiveAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Camera live view stop failed.");
        }
    }

    private void OnLiveViewChanged()
    {
        if (!PositionUpdatesActive)
            return;
        Application.Current.Dispatcher.BeginInvoke(RefreshLiveView);
    }

    private void RefreshLiveView()
    {
        OnPropertyChanged(nameof(Inspection));
        OnPropertyChanged(nameof(CameraError));
        if (!Inspection.IsLiveView)
        {
            lock (_liveImageGate)
            {
                LiveImage = null;
                _pendingLiveFrame = null;
            }
        }

        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        GrabCommand.NotifyCanExecuteChanged();
        ApplyLightCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CameraImage));
    }

    private async Task HandlePreviewFailureAsync(Exception exception)
    {
        _logger.LogError(exception, "Camera preview conversion failed.");
        await RequestCameraStopAsync();
        CameraError = exception.Message;
        lock (_liveImageGate)
        {
            _pendingLiveFrame = null;
            _liveImageUpdateQueued = false;
        }
    }

    private void UpdateLiveImage(ImageFrame frame)
    {
        if (!Inspection.IsLiveView)
        {
            return;
        }

        lock (_liveImageGate)
        {
            _pendingLiveFrame = frame;
            if (_liveImageUpdateQueued)
            {
                return;
            }

            _liveImageUpdateQueued = true;
            _liveImageUpdate = Task.Run(UpdateLiveImagesAsync);
        }
    }

    private async Task UpdateLiveImagesAsync()
    {
        try
        {
            while (true)
            {
                ImageFrame frame;
                lock (_liveImageGate)
                {
                    if (_pendingLiveFrame is null)
                    {
                        _liveImageUpdateQueued = false;
                        return;
                    }

                    frame = _pendingLiveFrame;
                    _pendingLiveFrame = null;
                }

                var image = InspectionPreview.CreateBitmap(frame);
                // Frozen frames can cross threads; WPF marshals the scalar binding.
                lock (_liveImageGate)
                {
                    if (Inspection.IsLiveView && IsInspectionSelected)
                        LiveImage = image;
                }
            }
        }
        catch (Exception exception)
        {
            await Application.Current.Dispatcher.InvokeAsync(
                () => HandlePreviewFailureAsync(exception)).Task.Unwrap();
        }
    }
}
