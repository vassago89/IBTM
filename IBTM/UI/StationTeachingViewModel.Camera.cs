using System;
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
            RecipeEditor.ClearCarrierImages();
            await Task.Run(
                () =>
                {
                    foreach (var image in images)
                    {
                        RecipeEditor.SaveCarrierImage(
                            image.Center,
                            image.Image);
                    }
                });

            await RecipeEditor.SaveAsync();
            completed = true;
            SelectedPoint = FilteredPoints.FirstOrDefault(point =>
                point.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        }
        catch (OperationCanceledException)
        {
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
        && !IsCameraLive
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
        _teachingPoints.ApplyImage(
            CurrentRecipe,
            FilteredPoints,
            point,
            new AxisPosition
            {
                X = imagePoint.X,
                Y = imagePoint.Y,
            });
        if (point.Target is
            TeachingTarget.CarrierUpperLeftLocatingPin
            or TeachingTarget.CarrierLowerRightLocatingPin)
        {
            await _teachingPoints.SaveAsync(point);
            OnPropertyChanged(nameof(CarrierOrigin));
        }
        else if (point.Target == TeachingTarget.BoltReference)
        {
            await RecipeEditor.SaveAsync();
        }

        SelectedPoint = point.Target switch
        {
            TeachingTarget.CarrierUpperLeftLocatingPin =>
                FilteredPoints.FirstOrDefault(candidate =>
                    candidate.Target
                        == TeachingTarget.CarrierLowerRightLocatingPin),
            TeachingTarget.CarrierLowerRightLocatingPin =>
                FilteredPoints.FirstOrDefault(candidate =>
                    candidate.Target == TeachingTarget.BoltReference),
            _ => SelectedPoint,
        };
        RefreshImageMarkers();
    }

    private bool CanTeachImagePoint(Point _) =>
        CanUseCurrentHandler()
        && HasCarrierImages
        && !IsCameraLive
        && SelectedPoint?.TeachMode == TeachMode.Image
        && (SelectedPoint.Target != TeachingTarget.BoltReference
            || _teachingPoints.CarrierReferenceReady)
        && (SelectedPoint.Target
                != TeachingTarget.CarrierLowerRightLocatingPin
            || _carrierReference.UpperLeftLocatingPin is not null);

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
        finally
        {
            lock (_liveImageGate)
            {
                _pendingLiveFrame = null;
            }
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
                try
                {
                    HandleLiveViewFailure(exception);
                }
                finally
                {
                    lock (_liveImageGate)
                    {
                        _pendingLiveFrame = null;
                        _liveImageUpdateQueued = false;
                    }
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
