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

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private void ToggleLiveView()
    {
        if (IsCameraLive)
        {
            StopCamera();
            IsCameraLive = false;
            ShowRecipeImages();
        }
        else
        {
            StartCamera();
            IsCameraLive = true;
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

            CarrierImages = [.. images];
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
                },
                motionCancellation.Token);

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
        _pointMapper.ApplyImage(
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
            await _carrierReference.SaveAsync();
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
            || _pointMapper.CarrierReferenceReady)
        && (SelectedPoint.Target
                != TeachingTarget.CarrierLowerRightLocatingPin
            || _carrierReference.UpperLeftLocatingPin is not null);

    private void StartCamera()
    {
        _boltInspector.StartLiveView();
    }

    private void StopCamera()
    {
        try
        {
            _boltInspector.StopLiveView();
        }
        finally
        {
            lock (_liveImageGate)
            {
                _pendingLiveFrame = null;
            }
        }
    }

    private void UpdateLiveImage(ImageFrame frame)
    {
        lock (_liveImageGate)
        {
            _pendingLiveFrame = frame;
            if (_liveImageUpdateQueued)
            {
                return;
            }

            _liveImageUpdateQueued = true;
        }

        _ = Task.Run(ConvertLiveImage);
    }

    private void ConvertLiveImage()
    {
        ImageFrame? frame;
        lock (_liveImageGate)
        {
            frame = _pendingLiveFrame;
            _pendingLiveFrame = null;
        }

        var image = frame is null ? null : ToBitmapSource(frame);
        RunOnUi(() => ApplyLiveImage(image));
    }

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);

    private void ApplyLiveImage(BitmapSource? image)
    {
        if (IsCameraLive && IsInspectionSelected && image is not null)
        {
            LiveImage = image;
        }

        lock (_liveImageGate)
        {
            if (_pendingLiveFrame is null)
            {
                _liveImageUpdateQueued = false;
                return;
            }
        }

        _ = Task.Run(ConvertLiveImage);
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
