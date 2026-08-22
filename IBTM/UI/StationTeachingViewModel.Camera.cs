using System;
using System.Collections.Generic;
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
    private ICamera? CurrentCamera => GetCamera(SelectedMotionGroup);

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private void ToggleLiveView()
    {
        if (IsCameraLive)
        {
            StopCamera(SelectedMotionGroup);
            IsCameraLive = false;
            ShowRecipeImages();
        }
        else
        {
            StartCamera(SelectedMotionGroup);
            IsCameraLive = true;
        }
    }

    private bool CanToggleLiveView() =>
        CurrentCamera is not null
        && (IsCameraLive || CanUseCurrentHandler());

    [RelayCommand(CanExecute = nameof(CanCaptureCarrierImages))]
    private async Task CaptureCarrierImagesAsync(
        CancellationToken cancellationToken)
    {
        var channel = GetLightChannel(MotionGroup.InspectionGantry);
        var scanStarted = false;
        try
        {
            using var motionCancellation = LinkMotion(cancellationToken);
            var motion = GetMotion(MotionGroup.InspectionGantry);
            var xPositions = ScanPositions(
                _inspectionGantrySettings.CarrierScanUpperLeft.X,
                _inspectionGantrySettings.CarrierScanLowerRight.X,
                ScanPitchX);
            var yPositions = ScanPositions(
                _inspectionGantrySettings.CarrierScanUpperLeft.Y,
                _inspectionGantrySettings.CarrierScanLowerRight.Y,
                ScanPitchY);

            await _inspectionGantrySettings.SaveAsync(
                motionCancellation.Token);
            RecipeEditor.ClearCarrierImages();
            scanStarted = true;
            CarrierImages = [];
            LiveImage = null;
            _light.SetLevel(channel, GetLightLevel(MotionGroup.InspectionGantry));
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
                    var tile = RecipeEditor.SaveCarrierImage(
                        center,
                        ToBitmapSource(_inspectionCamera.Capture()));
                    CarrierImages = [.. CarrierImages, tile];
                }
            }

        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _light.TurnOff(channel);
            if (scanStarted)
            {
                await RecipeEditor.SaveAsync();
            }
        }
    }

    private bool CanCaptureCarrierImages() =>
        SelectedMotionGroup == MotionGroup.InspectionGantry
        && !IsCameraLive
        && ScanPitchX > 0
        && ScanPitchY > 0
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
        if (point.Storage == TeachingStorage.Machine)
        {
            await _inspectionGantrySettings.SaveAsync();
        }
        RefreshImageMarkers();
    }

    private bool CanTeachImagePoint(Point imagePoint) =>
        HasCarrierImages
        && !IsCameraLive
        && SelectedPoint?.TeachMode == TeachMode.Image
        && (SelectedPoint.Target != TeachingTarget.BoltReference
            || _pointMapper.CarrierReferenceReady);

    private static IReadOnlyList<double> ScanPositions(
        double start,
        double end,
        double pitch)
    {
        var distance = Math.Abs(end - start);
        var count = Math.Max(1, (int)Math.Ceiling(distance / pitch) + 1);
        var direction = Math.Sign(end - start);
        var positions = new double[count];
        for (var index = 0; index < count; index++)
        {
            positions[index] = index == count - 1
                ? end
                : start + (direction * pitch * index);
        }

        return positions;
    }

    private ICamera? GetCamera(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbPlacementHandler => _alignmentCamera,
        MotionGroup.InspectionGantry => _inspectionCamera,
        _ => null,
    };

    private void StartCamera(MotionGroup motionGroup)
    {
        var channel = GetLightChannel(motionGroup);
        _light.SetLevel(channel, GetLightLevel(motionGroup));
        _light.TurnOn(channel);
        GetCamera(motionGroup)!.StartLiveView();
    }

    private void StopCamera(MotionGroup motionGroup)
    {
        GetCamera(motionGroup)!.StopLiveView();
        _light.TurnOff(GetLightChannel(motionGroup));
    }

    private int GetLightChannel(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbPlacementHandler => _lighting.AlignmentChannel,
        MotionGroup.InspectionGantry => _lighting.InspectionChannel,
        _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
    };

    private int GetLightLevel(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbPlacementHandler => _lighting.AlignmentLevel,
        MotionGroup.InspectionGantry => _lighting.InspectionLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
    };

    private void UpdateLiveImage(MotionGroup motionGroup, ImageFrame frame) =>
        RunOnUi(() =>
        {
            if (SelectedMotionGroup == motionGroup)
            {
                LiveImage = ToBitmapSource(frame);
            }
        });

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
