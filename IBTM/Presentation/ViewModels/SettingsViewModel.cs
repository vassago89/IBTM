using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Configuration;
using IBTM.Core.Geometry;
using IBTM.Core.Machine;
using IBTM.Device;
using IBTM.Infrastructure.Persistence;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Presentation.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly double[] JogSpeeds = [1.0, 10.0, 50.0];
    private static readonly int[] LaserChannels =
        [PcbPlacementStation.LaserChannel, BoltFasteningStation.LaserChannel, InspectionStation.LaserChannel];

    private readonly IMotionService[] _motions;
    private readonly IIoService _io;
    private readonly RecipeService _recipes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMotionParams))]
    private int _selectedZone = 1;

    [ObservableProperty] private double _currentX;
    [ObservableProperty] private double _currentY;
    [ObservableProperty] private double _currentZ;
    [ObservableProperty] private int _jogSpeedIndex = 1;
    [ObservableProperty] private bool _laserOn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeOffsetsCommand))]
    private bool _zone1RefRecorded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeOffsetsCommand))]
    private bool _zone2RefRecorded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeOffsetsCommand))]
    private bool _zone3RefRecorded;

    [ObservableProperty] private string _offsetResultText = "—";
    [ObservableProperty] private string _statusMessage = string.Empty;

    public SettingsViewModel(
        [FromKeyedServices(1)] IMotionService zone1,
        [FromKeyedServices(2)] IMotionService zone2,
        [FromKeyedServices(3)] IMotionService zone3,
        IIoService io,
        RecipeService recipes,
        MachineConfig config)
    {
        _motions = [zone1, zone2, zone3];
        _io = io;
        _recipes = recipes;
        Config = config;

        zone1.PositionChanged += OnZone1PositionChanged;
        zone2.PositionChanged += OnZone2PositionChanged;
        zone3.PositionChanged += OnZone3PositionChanged;
    }

    public MachineConfig Config { get; }
    public double JogSpeed => JogSpeeds[JogSpeedIndex];
    public ZoneMotionParams CurrentMotionParams => SelectedZone switch
    {
        1 => Config.PcbPlacementMotion,
        2 => Config.BoltFastening.Motion,
        3 => Config.Inspection.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(SelectedZone)),
    };

    private IMotionService CurrentMotion => _motions[SelectedZone - 1];
    private int LaserChannel => LaserChannels[SelectedZone - 1];

    public void Activate()
    {
        UpdateReferenceStatus();
        UpdateOffsetText();
        RefreshPosition();
        StatusMessage = "Configuration loaded";
    }

    partial void OnSelectedZoneChanged(int oldValue, int newValue)
    {
        if (LaserOn)
        {
            _io.SetOutput(LaserChannels[oldValue - 1], false);
            LaserOn = false;
        }

        _motions[oldValue - 1].Stop();
        RefreshPosition();
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        try
        {
            await _recipes.SaveConfigAsync(Config);
            StatusMessage = "Configuration saved";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Configuration save failed: {exception.Message}";
        }
    }

    [RelayCommand] private void JogXPlus() => CurrentMotion.JogX(JogSpeed);
    [RelayCommand] private void JogXMinus() => CurrentMotion.JogX(-JogSpeed);
    [RelayCommand] private void JogYPlus() => CurrentMotion.JogY(JogSpeed);
    [RelayCommand] private void JogYMinus() => CurrentMotion.JogY(-JogSpeed);
    [RelayCommand] private void JogZPlus() => CurrentMotion.JogZ(JogSpeed);
    [RelayCommand] private void JogZMinus() => CurrentMotion.JogZ(-JogSpeed);
    [RelayCommand] private void JogStop() => CurrentMotion.Stop();

    [RelayCommand]
    private void ToggleLaser()
    {
        LaserOn = !LaserOn;
        _io.SetOutput(LaserChannel, LaserOn);
    }

    [RelayCommand]
    private void RecordRef()
    {
        var current = CurrentMotion.GetPosition();
        var position = new AxisPos { X = current.X, Y = current.Y, Z = current.Z };

        switch (SelectedZone)
        {
            case 1:
                Config.Calibration.Zone1Ref = position;
                Zone1RefRecorded = true;
                break;
            case 2:
                Config.Calibration.Zone2Ref = position;
                Zone2RefRecorded = true;
                break;
            case 3:
                Config.Calibration.Zone3Ref = position;
                Zone3RefRecorded = true;
                break;
        }

        OnPropertyChanged(nameof(Config));
        StatusMessage = $"Zone {SelectedZone} reference recorded";
    }

    [RelayCommand(CanExecute = nameof(CanComputeOffsets))]
    private void ComputeOffsets()
    {
        Config.Calibration.ComputeOffsets();
        OnPropertyChanged(nameof(Config));
        UpdateOffsetText();
        StatusMessage = "Offsets computed";
    }

    private bool CanComputeOffsets() =>
        Zone1RefRecorded && Zone2RefRecorded && Zone3RefRecorded;

    public void Deactivate()
    {
        foreach (var motion in _motions)
        {
            motion.Stop();
        }

        if (LaserOn)
        {
            _io.SetOutput(LaserChannel, false);
            LaserOn = false;
        }
    }

    public void Dispose()
    {
        Deactivate();
        _motions[0].PositionChanged -= OnZone1PositionChanged;
        _motions[1].PositionChanged -= OnZone2PositionChanged;
        _motions[2].PositionChanged -= OnZone3PositionChanged;
    }

    private void UpdateReferenceStatus()
    {
        Zone1RefRecorded = IsSet(Config.Calibration.Zone1Ref);
        Zone2RefRecorded = IsSet(Config.Calibration.Zone2Ref);
        Zone3RefRecorded = IsSet(Config.Calibration.Zone3Ref);
    }

    private static bool IsSet(AxisPos position) =>
        position.X != 0 || position.Y != 0 || position.Z != 0;

    private void UpdateOffsetText() =>
        OffsetResultText =
            $"3→1: dX={Config.Calibration.Offset3To1.X:F3} dY={Config.Calibration.Offset3To1.Y:F3}  "
            + $"3→2: dX={Config.Calibration.Offset3To2.X:F3} dY={Config.Calibration.Offset3To2.Y:F3}";

    private void RefreshPosition()
    {
        var current = CurrentMotion.GetPosition();
        CurrentX = current.X;
        CurrentY = current.Y;
        CurrentZ = current.Z;
    }

    private void OnZone1PositionChanged(double x, double y, double z) =>
        ApplyPosition(1, x, y, z);

    private void OnZone2PositionChanged(double x, double y, double z) =>
        ApplyPosition(2, x, y, z);

    private void OnZone3PositionChanged(double x, double y, double z) =>
        ApplyPosition(3, x, y, z);

    private void ApplyPosition(int zone, double x, double y, double z) =>
        RunOnUi(() =>
        {
            if (SelectedZone == zone)
            {
                CurrentX = x;
                CurrentY = y;
                CurrentZ = z;
            }
        });

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
