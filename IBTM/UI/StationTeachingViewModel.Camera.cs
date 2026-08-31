using System;
using System.Collections.Generic;
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
        var channel = _lighting.InspectionChannel;
        var completed = false;
        try
        {
            using var motionCancellation = LinkMotion(cancellationToken);
            var motion = GetMotion(MotionGroup.InspectionGantry);
            var xPositions = ScanPositions(
                _inspectionGantrySettings.CarrierScanUpperLeft.X,
                _inspectionGantrySettings.CarrierScanLowerRight.X,
                _inspectionCameraSettings.FieldOfViewWidthMillimeters
                    - ScanOverlap);
            var yPositions = ScanPositions(
                _inspectionGantrySettings.CarrierScanUpperLeft.Y,
                _inspectionGantrySettings.CarrierScanLowerRight.Y,
                _inspectionCameraSettings.FieldOfViewHeightMillimeters
                    - ScanOverlap);

            await _inspectionGantrySettings.SaveAsync(
                motionCancellation.Token);
            var images = new List<CarrierImageTileView>();
            CarrierImages = [];
            LiveImage = null;
            _light.SetLevel(channel, _lighting.InspectionLevel);
            _light.TurnOn(channel);

            for (var row = 0; row < yPositions.Count; row++)
            {
                for (var column = 0; column < xPositions.Count; column++)
                {
                    var xIndex = row % 2 == 0
                        ? column
                        : xPositions.Count - column - 1;
                    var center = new AxisPos
                    {
                        X = xPositions[xIndex],
                        Y = yPositions[row],
                    };
                    await motion.MoveToXYAsync(
                        center.X,
                        center.Y,
                        _inspectionGantrySettings.Motion.HorizontalSpeed,
                        motionCancellation.Token);
                    images.Add(new CarrierImageTileView(
                        images.Count + 1,
                        center,
                        ToBitmapSource(_inspectionCamera.Capture())));
                    CarrierImages = [.. images];
                }
            }

            RecipeEditor.ClearCarrierImages();
            foreach (var image in images)
            {
                RecipeEditor.SaveCarrierImage(
                    image.Center,
                    image.Image);
            }

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
            _light.TurnOff(channel);
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
            new AxisPos
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
            || _carrierReference.UpperLeftPin is not null);

    private static IReadOnlyList<double> ScanPositions(
        double start,
        double end,
        double pitch)
    {
        var distance = Math.Abs(end - start);
        if (distance == 0)
        {
            return [start];
        }

        var segments = (int)Math.Ceiling(distance / pitch);
        var positions = new double[segments + 1];
        for (var index = 0; index <= segments; index++)
        {
            positions[index] =
                start + ((end - start) * index / segments);
        }

        return positions;
    }

    private void StartCamera()
    {
        var channel = _lighting.InspectionChannel;
        _light.SetLevel(channel, _lighting.InspectionLevel);
        _light.TurnOn(channel);
        try
        {
            _inspectionCamera.StartLiveView();
        }
        catch
        {
            _light.TurnOff(channel);
            throw;
        }
    }

    private void StopCamera()
    {
        try
        {
            _inspectionCamera.StopLiveView();
        }
        finally
        {
            _light.TurnOff(_lighting.InspectionChannel);
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

        RunOnUi(ApplyLiveImage);
    }

    private void ApplyLiveImage()
    {
        ImageFrame? frame;
        lock (_liveImageGate)
        {
            frame = _pendingLiveFrame;
            _pendingLiveFrame = null;
            _liveImageUpdateQueued = false;
        }

        if (IsCameraLive && IsInspectionSelected && frame is not null)
        {
            LiveImage = ToBitmapSource(frame);
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
