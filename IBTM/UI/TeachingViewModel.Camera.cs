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

namespace IBTM.UI;

public partial class TeachingViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrawFovRegionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRulerResolutionCommand))]
    public partial bool IsMeasuring { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulerResolution))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRulerResolutionCommand))]
    public partial ImageRuler? Ruler { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulerResolution))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRulerResolutionCommand))]
    public partial double? RulerMillimeters { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DrawFovRegionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadDataMatrixCommand))]
    public partial CarrierImageTileView? SelectedFov { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FovRoiLabel))]
    [NotifyCanExecuteChangedFor(nameof(ReadDataMatrixCommand))]
    public partial Rect? FovRegion { get; set; }

    [ObservableProperty]
    public partial string? DataMatrixResult { get; set; }

    private readonly object _liveImageGate;
    private ImageFrame? _pendingLiveFrame;
    private bool _liveImageUpdateQueued;
    private Task _liveImageUpdate = Task.CompletedTask;
    private Task _cameraStop = Task.CompletedTask;

    public double? RulerResolution
    {
        get
        {
            if (Ruler is not { PixelLength: >= 1 } ruler || RulerMillimeters is not > 0)
                return null;
            var resolution = RulerMillimeters.Value / ruler.PixelLength;
            return double.IsFinite(resolution) && resolution > 0 ? resolution : null;
        }
    }

    public string FovRoiLabel
    {
        get
        {
            var metadata = SelectedFov?.Metadata;
            if (metadata is { IsBarcode: true } barcode)
                return $"{barcode.HeatSink.GetDescription()} · Data Matrix · ROI size saves when you finish dragging.";
            if (metadata?.BoltNumber is { } number)
                return $"{metadata.HeatSink.GetDescription()} · Bolt {number} · ROI size saves when you finish dragging.";
            return Preview.HasImage
                ? "Resize the ROI, select a bolt or Data Matrix, then Record Position to save the image, ROI and current coordinates."
                : "Grab an image to start. A centered ROI is created automatically.";
        }
    }

    public IRelayCommand<ImageRuler> MeasureImageCommand { get; }

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        ReinspectImageCommand.NotifyCanExecuteChanged();
        if (e.PropertyName != nameof(InspectionPreview.Image))
            return;
        Ruler = null;
        RulerMillimeters = null;
        MeasureImageCommand.NotifyCanExecuteChanged();
        ApplyRulerResolutionCommand.NotifyCanExecuteChanged();
        DrawFovRegionCommand.NotifyCanExecuteChanged();
        ReadDataMatrixCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FovRoiLabel));
    }

    private void MeasureImage(ImageRuler? ruler)
    {
        if (ruler is { PixelLength: >= 1 })
            Ruler = ruler;
    }

    private bool IsMeasureImageAllowed(ImageRuler? ruler)
    {
        if (ruler is null
            || !IsInspectionSelected
            || !IsMeasuring
            || Preview.Image is not { } image)
            return false;
        var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
        return bounds.Contains(ruler.Start) && bounds.Contains(ruler.End);
    }

    public IAsyncRelayCommand ApplyRulerResolutionCommand { get; }

    private async Task ApplyRulerResolutionAsync(CancellationToken cancellationToken)
    {
        var resolution = RulerResolution!.Value;
        CameraError = null;
        try
        {
            var viewToken = ViewCancellation;
            var activeToken = cancellationToken;
            try
            {
                if (!(State.SetupEditingEnabled))
                    return;
                using var operation = Machine.BeginManualOperation(
                    () => State.ManualMode,
                    cancellationToken,
                    viewToken);
                if (operation is null)
                    return;
                activeToken = operation.Token;
                operation.Token.ThrowIfCancellationRequested();
                MillimetersPerPixel = resolution;
                await RecipeEditor.SaveAsync(operation.Token);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || ViewCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CameraError = exception.Message;
        }
    }

    private bool IsApplyRulerResolutionAllowed
    {
        get
        {
            return IsTeachingEditAllowed && IsInspectionSelected && IsMeasuring
                && Preview.HasImage && RulerResolution is not null
                && RecipeEditor.IsSaveAllowed && CarrierImages.Count == Recipes.Current.CarrierImages.Count;
        }
    }

    partial void OnSelectedFovChanged(CarrierImageTileView? value)
    {
        ApplyRulerResolutionCommand.Cancel();
        Ruler = null;
        RulerMillimeters = null;
        CaptureInspectionCommand.Cancel();
        ReinspectImageCommand.Cancel();
        var metadata = value?.Metadata;
        ReadDataMatrixCommand.Cancel();
        DataMatrixResult = null;
        var region = metadata?.Region;
        if (region is null && value is not null)
        {
            var width = value.Image.PixelWidth;
            var height = value.Image.PixelHeight;
            region = PixelRegion.CenteredSquare(width, height, Math.Min(width, height) / 4);
        }
        var bounds = region is not null
            ? new Rect(region.X, region.Y, region.Width, region.Height)
            : (Rect?)null;
        Preview.Clear(SelectedBarcode, SelectedPoint?.Position.Bolt);
        FovRegion = bounds;
        if (value is not null)
            RefreshPreview(value.Image);
        OnPropertyChanged(nameof(FovRoiLabel));
        MeasureImageCommand.NotifyCanExecuteChanged();
        NotifyManualTeachingCommands();
    }

    partial void OnFovRegionChanged(Rect? value)
    {
        CaptureInspectionCommand.Cancel();
        ReinspectImageCommand.Cancel();
        ReadDataMatrixCommand.Cancel();
        DataMatrixResult = null;
        RefreshPreview();
    }

    private void SelectFovForTeachingPoint()
    {
        var fov = IsInspectionSelected
            ? CarrierImages.FirstOrDefault(image => image.Metadata.HeatSink == SelectedPcb
                && (SelectedBarcode is not null
                    ? image.Metadata.IsBarcode
                    : IsBoltSelected && !image.Metadata.IsBarcode
                        && image.Metadata.BoltNumber == SelectedPoint!.BoltNumber))
            : null;
        if (SelectedFov != fov)
            SelectedFov = fov;
        else if (fov is not null)
            OnSelectedFovChanged(fov);
        else
            RefreshPreview();
    }

    private void RefreshPreview(BitmapSource? image = null)
    {
        image ??= Preview.Image;
        Preview.Clear(SelectedBarcode, SelectedPoint?.Position.Bolt);
        if (!IsInspectionSelected || image is null)
            return;
        try
        {
            var region = FovRegion is { Width: >= 1, Height: >= 1 } bounds
                ? new PixelRegion(
                    (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                    (int)Math.Ceiling(bounds.Right) - (int)Math.Floor(bounds.Left),
                    (int)Math.Ceiling(bounds.Bottom) - (int)Math.Floor(bounds.Top))
                : null;
            Preview.SetSavedImage(image, region);
            CameraError = null;
        }
        catch (Exception exception)
        {
            CameraError = exception.Message;
        }
    }

    public IAsyncRelayCommand ReadDataMatrixCommand { get; }

    private async Task ReadDataMatrixAsync(CancellationToken cancellationToken)
    {
        var image = Preview.Image!;
        var bounds = FovRegion!.Value;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ViewCancellation);
        DataMatrixResult = "Reading…";
        CameraError = null;
        try
        {
            var text = await Task.Run(
                () =>
                {
                    var frame = InspectionPreview.CreateFrame(image);
                    var left = (int)Math.Floor(bounds.Left);
                    var top = (int)Math.Floor(bounds.Top);
                    var region = new PixelRegion(
                        left, top,
                        (int)Math.Ceiling(bounds.Right) - left,
                        (int)Math.Ceiling(bounds.Bottom) - top);
                    return DataMatrixReader.Read(frame, region);
                },
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            DataMatrixResult = string.IsNullOrEmpty(text) ? "Not Read" : text;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DataMatrixResult = null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Teaching Data Matrix read failed. {0}", exception);
            DataMatrixResult = null;
            CameraError = exception.Message;
        }
    }

    private bool IsReadDataMatrixAllowed
    {
        get
        {
            return IsInspectionSelected
                && SelectedBarcode is not null
                && Preview.Image is { } image
                && FovRegion is { Width: >= 1, Height: >= 1 } bounds
                && bounds.Left >= 0 && bounds.Top >= 0
                && bounds.Right <= image.PixelWidth
                && bounds.Bottom <= image.PixelHeight;
        }
    }

    public IAsyncRelayCommand<Rect> DrawFovRegionCommand { get; }

    private async Task DrawFovRegionAsync(Rect bounds)
    {
        if (bounds.IsEmpty || !IsDrawFovRegionAllowed(bounds))
            return;
        var image = Preview.Image!;
        var region = PixelRegion.CenteredSquare(
            image.PixelWidth, image.PixelHeight, (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height)));
        FovRegion = new Rect(region.X, region.Y, region.Width, region.Height);
        if (SelectedFov is not { } fov)
            return;
        var barcode = SelectedBarcode;
        var viewToken = ViewCancellation;
        var activeToken = CancellationToken.None;
        try
        {
            if (!(State.SetupEditingEnabled))
                return;
            using var operation = Machine.BeginManualOperation(() => State.ManualMode, CancellationToken.None, viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            fov.Metadata.Region = PixelRegion.CenteredSquare(
                fov.Image.PixelWidth, fov.Image.PixelHeight, region.Width);
            RefreshPointPositions();
            await RecipeEditor.SaveAsync(operation.Token);
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

        if (barcode is not null && ReadDataMatrixCommand.CanExecute(null))
            await ReadDataMatrixCommand.ExecuteAsync(null);
    }

    private bool IsDrawFovRegionAllowed(Rect bounds)
    {
        return IsTeachingEditAllowed
            && IsInspectionSelected
            && !IsMeasuring
            && RecipeEditor.IsSaveAllowed
            && Preview.HasImage
            && (bounds.IsEmpty || bounds.Width >= 1 && bounds.Height >= 1);
    }

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
            SelectedCameraTab = 0;
            await Inspection.StartLiveViewAsync(cancellation.Token);
            if (!State.ManualMode || !IsInspectionSelected)
                await StopCameraLiveAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Camera live view operation failed. {0}", exception);
        }
    }

    private bool IsToggleLiveViewAllowed
    {
        get
        {
            return Inspection.IsLiveView
                || IsInspectionSelected
                    && State.ManualMode
                    && !TeachCurrentPositionCommand.IsRunning
                    && !GrabCommand.IsRunning
                    && !CaptureInspectionCommand.IsRunning;
        }
    }

    private void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning))
            return;
        OnPropertyChanged(nameof(IsBusy));
        if (ReferenceEquals(sender, TeachCurrentPositionCommand)
            || ReferenceEquals(sender, GrabCommand)
            || ReferenceEquals(sender, CaptureInspectionCommand))
            ToggleLiveViewCommand.NotifyCanExecuteChanged();
        if (ReferenceEquals(sender, ToggleLiveViewCommand))
            GrabCommand.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand GrabCommand { get; }

    private bool IsGrabAllowed => IsInspectionSelected
        && IsTeachingEditAllowed
        && !State.IsRunning
        && !ToggleLiveViewCommand.IsRunning;

    private async Task GrabAsync(CancellationToken cancellationToken)
    {
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!IsGrabAllowed)
                return;
            using var operation = Machine.BeginManualOperation(
                () => State.ManualMode,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            CameraError = null;
            SelectedCameraTab = 0;
            await _recipeImageUpdate;
            operation.Token.ThrowIfCancellationRequested();
            var frame = await Inspection.CaptureCurrentAsync(operation.Token, keepLiveView: true);
            var image = await Task.Run(() => InspectionPreview.CreateBitmap(frame), operation.Token);
            operation.Token.ThrowIfCancellationRequested();

            // Grab prepares the image and ROI; only Record Position changes teaching coordinates.
            IsMeasuring = false;
            ReadDataMatrixCommand.Cancel();
            DataMatrixResult = null;
            Preview.Clear(SelectedBarcode, SelectedPoint?.Position.Bolt);
            if (FovRegion is null)
            {
                var region = PixelRegion.CenteredSquare(
                    image.PixelWidth, image.PixelHeight, Math.Min(image.PixelWidth, image.PixelHeight) / 4);
                FovRegion = new Rect(region.X, region.Y, region.Width, region.Height);
            }
            RefreshPreview(image);
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(MachineAlarm.Inspection, exception);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Teaching image grab failed. {0}", exception);
            if (!activeToken.IsCancellationRequested)
                CameraError = exception.Message;
        }
    }

    private async Task RecordImagePositionAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint;
        var bolt = point?.Position.Bolt;
        var barcode = SelectedBarcode is not null;
        if (bolt is null && !barcode)
            return;
        var pcb = SelectedPcb;
        var roi = FovRegion;
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            CameraError = null;
            if (bolt is not null && !_carrierReference.IsDefined)
                throw new InvalidOperationException("Record the Inspection Gantry Upper/Lower references before recording a bolt position.");
            var origin = _carrierReference.UpperLeftLocatingPin;
            await _recipeImageUpdate;
            operation.Token.ThrowIfCancellationRequested();
            if (CarrierImages.Count != Recipes.Current.CarrierImages.Count)
                throw new InvalidOperationException("Wait for the saved teaching images to load before recording a position.");
            var captured = await Inspection.CaptureCarrierImageAsync(operation.Token);
            var image = await Task.Run(() => InspectionPreview.CreateBitmap(captured.Frame), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var images = CarrierImages.ToList();
            var index = images.FindIndex(tile => tile.Metadata.HeatSink == pcb
                && (barcode ? tile.Metadata.IsBarcode : !tile.Metadata.IsBarcode && tile.Metadata.BoltNumber == bolt!.Number));
            var previous = index >= 0 ? images[index].Metadata : null;
            var metadata = new CarrierImageTile
            {
                Number = previous?.Number ?? (images.Count == 0 ? 1 : images.Max(tile => tile.Metadata.Number) + 1),
                Center = captured.Center,
                HeatSink = pcb,
                BoltNumber = bolt?.Number,
                IsBarcode = barcode,
                Region = PixelRegion.CenteredSquare(image.PixelWidth, image.PixelHeight,
                    roi is { Width: >= 1, Height: >= 1 } bounds
                        ? (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height))
                        : Math.Min(image.PixelWidth, image.PixelHeight) / 4),
            };
            var replacement = new CarrierImageTileView(metadata, image);
            if (index >= 0)
                images[index] = replacement;
            else
                images.Add(replacement);

            var previousX = bolt?.X;
            var previousY = bolt?.Y;
            if (bolt is not null)
            {
                // The bolt is centered on the camera crosshair. ROI pixels do not alter its machine XY.
                var position = CarrierCoordinates.FromMachine(captured.Center, origin!);
                bolt.X = position.X;
                bolt.Y = position.Y;
            }
            if (await RecipeEditor.SaveCarrierImagesAsync(images, operation.Token))
            {
                CarrierImages = images;
                RefreshPointPositions();
            }
            else if (bolt is not null)
            {
                bolt.X = previousX;
                bolt.Y = previousY;
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Teaching inspection failed. {0}", exception);
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

    public IAsyncRelayCommand CaptureInspectionCommand { get; }

    private async Task CaptureInspectionAsync(CancellationToken token)
    {
        SelectedCameraTab = 0;
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = token;
        try
        {
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                token,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await StopCameraLiveAsync();
            operation.Token.ThrowIfCancellationRequested();
            CameraError = null;
            var pcb = SelectedBarcode;
            var bolt = SelectedPoint!.Position.Bolt;
            var region = pcb is { } target ? Inspection.GetBarcodeFov(target).Region : Inspection.GetFov(bolt!).Region;
            Preview.Clear(pcb, bolt);
            var frame = pcb is { } barcode ? await Inspection.CaptureBarcodeAsync(barcode, operation.Token) : await Inspection.CaptureAsync(bolt!, operation.Token);
            await Preview.SetImageAsync(frame, operation.Token, region);
            await Preview.InspectAsync(operation.Token);
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Teaching inspection failed. {0}", exception);
            if (!activeToken.IsCancellationRequested)
                CameraError = exception.Message;
        }
    }

    private bool IsCaptureInspectionAllowed
    {
        get
        {
            return IsInspectionSelected
                && IsMoveToPointAllowed
                && (SelectedBarcode is { } pcb
                    ? Inspection.HasBarcodeRegion(pcb)
                    : SelectedPoint?.Position.Bolt is { } bolt && Inspection.HasRegion(bolt));
        }
    }

    public IAsyncRelayCommand ReinspectImageCommand { get; }

    private async Task ReinspectImageAsync(CancellationToken token)
    {
        CameraError = null;
        try
        {
            var viewToken = ViewCancellation;
            var activeToken = token;
            try
            {
                if (!(State.SetupEditingEnabled))
                    return;
                using var operation = Machine.BeginManualOperation(
                    () => State.ManualMode,
                    token,
                    viewToken);
                if (operation is null)
                    return;
                activeToken = operation.Token;
                operation.Token.ThrowIfCancellationRequested();
                await Preview.InspectAsync(operation.Token);
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
        catch (OperationCanceledException) when (token.IsCancellationRequested || ViewCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Image reinspection failed. {0}", exception);
            CameraError = exception.Message;
        }
    }

    private bool IsReinspectImageAllowed
    {
        get
        {
            return IsTeachingEditAllowed
                && (IsBoltSelected || IsDataMatrixSelected)
                && Preview.HasImage && Preview.Region is not null;
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
            System.Diagnostics.Trace.TraceError("Camera live view stop failed. {0}", exception);
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
    }

    private async Task HandlePreviewFailureAsync(Exception exception)
    {
        System.Diagnostics.Trace.TraceError("Camera preview conversion failed. {0}", exception);
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
