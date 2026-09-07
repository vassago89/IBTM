using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    private readonly object _liveImageGate = new();
    private ImageFrame? _pendingLiveFrame;
    private bool _liveImageUpdateQueued;
    private Task _liveImageUpdate = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private void ToggleLiveView()
    {
        if (IsCameraLive)
        {
            StopCamera();
            ShowRecipeImages();
        }
        else
        {
            CameraError = null;
            IsCameraLive = true;
            try
            {
                _boltInspector.StartLiveView();
            }
            catch (Exception exception)
            {
                HandleLiveViewFailure(exception);
            }
        }
    }

    private bool CanToggleLiveView() =>
        IsInspectionSelected
        && (IsCameraLive || CanUseCurrentHandler());

    [RelayCommand(CanExecute = nameof(CanCaptureCarrierImages))]
    private async Task CaptureCarrierImagesAsync(
        CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            using var motionCancellation = LinkMotion(cancellationToken);
            CameraError = null;
            if (IsCameraLive)
            {
                StopCamera();
                if (CameraError is not null) return;
            }
            await _inspectionGantrySettings.SaveAsync(
                motionCancellation.Token);
            CarrierImages = [];
            LiveImage = null;
            var captured = await _boltInspector
                .CaptureCarrierImagesAsync(motionCancellation.Token);
            var images = await Task.Run(
                () => captured
                    .Select((image, index) => new CarrierImageTileView(
                        index + 1,
                        image.Center,
                        ToBitmapSource(image.Frame)))
                    .ToArray(),
                motionCancellation.Token);

            motionCancellation.Token.ThrowIfCancellationRequested();
            CarrierImages = images;
            await RecipeEditor.SaveCarrierImagesAsync(images);
            completed = true;
            SelectedPoint = FilteredPoints.FirstOrDefault(point =>
                point.Target == TeachingTarget.BoltReference);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            CameraError = exception.Message;
        }
        finally
        {
            if (!completed)
            {
                ShowRecipeImages();
            }
        }
    }

    private bool CanCaptureCarrierImages() =>
        IsInspectionSelected
        && _inspectionGantry.CanMove
        && _carrierReference.IsDefined
        && MillimetersPerPixel > 0
        && ScanOverlap >= 0
        && ScanOverlap
            < _inspectionCameraSettings.FieldOfViewWidthMillimeters
        && ScanOverlap
            < _inspectionCameraSettings.FieldOfViewHeightMillimeters
        && !string.IsNullOrWhiteSpace(RecipeEditor.Name)
        && CanUseCurrentHandler();

    [RelayCommand(CanExecute = nameof(CanTeachImagePoint))]
    private async Task TeachImagePointAsync(Point imagePoint)
    {
        var point = SelectedPoint!;
        point.Teach(imagePoint.X, imagePoint.Y, 0);
        point.Apply();
        RefreshPointPositions();
        await RecipeEditor.SaveAsync();
        NotifyManualTeachingCommands();
    }

    private bool CanTeachImagePoint(Point _) =>
        CanUseCurrentHandler()
        && HasCarrierImages
        && !IsCameraLive
        && SelectedPoint?.TeachMode == TeachMode.Image
        && _carrierReference.IsDefined;

    private void StopCamera()
    {
        IsCameraLive = false;
        try
        {
            _boltInspector.StopLiveView();
        }
        catch (Exception exception)
        {
            CameraError = exception.Message;
        }
        lock (_liveImageGate)
        {
            _pendingLiveFrame = null;
        }
    }

    private void OnLiveViewFailed(Exception exception) =>
        Application.Current.Dispatcher.BeginInvoke(
            () => HandleLiveViewFailure(exception));

    private void HandleLiveViewFailure(Exception exception)
    {
        if (!IsCameraLive)
        {
            return;
        }

        StopCamera();
        CameraError = exception.Message;
        LiveImage = null;
    }

    private void UpdateLiveImage(ImageFrame frame)
    {
        if (!IsCameraLive)
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

                var image = ToBitmapSource(frame);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (IsCameraLive && IsInspectionSelected)
                    {
                        LiveImage = image;
                    }
                });
            }
        }
        catch (Exception exception)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HandleLiveViewFailure(exception);
                lock (_liveImageGate)
                {
                    _pendingLiveFrame = null;
                    _liveImageUpdateQueued = false;
                }
            });
        }
    }

    private static BitmapSource ToBitmapSource(ImageFrame frame)
    {
        var image = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgr24,
            palette: null,
            frame.Pixels,
            frame.Stride);
        image.Freeze();
        return image;
    }
}
