using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    private ICamera? CurrentCamera => GetCamera(SelectedMotionGroup);
    private OutputIo? CurrentLaser => GetLaser(SelectedMotionGroup);

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private void ToggleLiveView()
    {
        if (IsCameraLive)
        {
            StopCamera(SelectedMotionGroup);
            LiveImage = null;
        }
        else
        {
            StartCamera(SelectedMotionGroup);
        }

        IsCameraLive = !IsCameraLive;
    }

    private bool CanToggleLiveView() => CurrentCamera is not null;

    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    private async Task CameraClickAsync(
        Point clickPosition,
        CancellationToken cancellationToken)
    {
        var camera = CurrentCamera!;
        var pixelOffsetX = clickPosition.X - (camera.ImageWidth / 2.0);
        var pixelOffsetY = clickPosition.Y - (camera.ImageHeight / 2.0);
        var targetX = SelectedMotionGroup == MotionGroup.PcbPlacement
            ? CurrentX
              + (pixelOffsetX
                 * _settings.PcbPlacement.AlignmentXMillimetersPerPixel)
            : CurrentX + (pixelOffsetX / _settings.Inspection.PixelsPerMm);
        var targetY = SelectedMotionGroup == MotionGroup.PcbPlacement
            ? CurrentY
              + (pixelOffsetY
                 * _settings.PcbPlacement.AlignmentYMillimetersPerPixel)
            : CurrentY + (pixelOffsetY / _settings.Inspection.PixelsPerMm);

        try
        {
            await CurrentMotion.MoveToAsync(
                targetX,
                targetY,
                CurrentZ,
                cancellationToken);
            StatusMessage = $"Move to X:{targetX:F3} Y:{targetY:F3}";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Move stopped";
        }
    }

    private bool CanUseCamera() => IsCameraLive && CurrentCamera is not null;

    [RelayCommand(CanExecute = nameof(CanToggleLaser))]
    private void ToggleLaser()
    {
        var laser = CurrentLaser!.Value;
        LaserOn = !_io.GetOutput(laser);
        SetLaser(SelectedMotionGroup, LaserOn);
    }

    private bool CanToggleLaser() => HasLaser;

    private ICamera? GetCamera(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbPlacement => _alignmentCamera,
        MotionGroup.Inspection => _inspectionCamera,
        _ => null,
    };

    private static OutputIo? GetLaser(MotionGroup motionGroup) =>
        motionGroup switch
        {
            MotionGroup.PcbPlacement => OutputIo.PcbPlacementLaser,
            MotionGroup.Inspection => OutputIo.InspectionLaser,
            _ => null,
        };

    private void SetLaser(MotionGroup motionGroup, bool on)
    {
        switch (motionGroup)
        {
            case MotionGroup.PcbPlacement:
                _pcbPlacement.SetLaser(on);
                break;
            case MotionGroup.Inspection:
                _inspection.SetLaser(on);
                break;
        }
    }

    private void StartCamera(MotionGroup motionGroup)
    {
        var channel = GetLightChannel(motionGroup);
        _light.SetLevel(channel, GetLightLevel(motionGroup));
        _light.TurnOn(channel);
        GetCamera(motionGroup)!.StartLiveView();
    }

    private void StopCamera(MotionGroup motionGroup)
    {
        GetCamera(motionGroup)?.StopLiveView();

        if (motionGroup is MotionGroup.PcbPlacement or MotionGroup.Inspection)
        {
            _light.TurnOff(GetLightChannel(motionGroup));
        }
    }

    private int GetLightChannel(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbPlacement => _lighting.AlignmentChannel,
        MotionGroup.Inspection => _lighting.InspectionChannel,
        _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
    };

    private int GetLightLevel(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbPlacement => _lighting.AlignmentLevel,
        MotionGroup.Inspection => _lighting.InspectionLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
    };

    private void UpdateLiveImage(MotionGroup motionGroup, ImageFrame frame) =>
        RunOnUi(() =>
        {
            if (SelectedMotionGroup == motionGroup)
            {
                LiveImage = frame.ToImageSource();
            }
        });

    private void OnOutputChanged(OutputIo output, bool value) =>
        RunOnUi(() =>
        {
            if (output == CurrentLaser)
            {
                LaserOn = value;
            }
        });
}
