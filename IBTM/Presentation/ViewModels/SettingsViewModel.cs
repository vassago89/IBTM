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
    private int _selectedStation = 1;

    [ObservableProperty] private double _currentX;
    [ObservableProperty] private double _currentY;
    [ObservableProperty] private double _currentZ;
    [ObservableProperty] private int _jogSpeedIndex = 1;
    [ObservableProperty] private bool _laserOn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeOffsetsCommand))]
    private bool _pcbPlacementReferenceRecorded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeOffsetsCommand))]
    private bool _boltFasteningReferenceRecorded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeOffsetsCommand))]
    private bool _inspectionReferenceRecorded;

    [ObservableProperty] private string _offsetResultText = "—";
    [ObservableProperty] private string _statusMessage = string.Empty;

    public SettingsViewModel(
        [FromKeyedServices(1)] IMotionService pcbPlacementMotion,
        [FromKeyedServices(2)] IMotionService boltFasteningMotion,
        [FromKeyedServices(3)] IMotionService inspectionMotion,
        IIoService io,
        RecipeService recipes,
        MachineConfig config)
    {
        _motions = [pcbPlacementMotion, boltFasteningMotion, inspectionMotion];
        _io = io;
        _recipes = recipes;
        Config = config;

        pcbPlacementMotion.PositionChanged += OnPcbPlacementPositionChanged;
        boltFasteningMotion.PositionChanged += OnBoltFasteningPositionChanged;
        inspectionMotion.PositionChanged += OnInspectionPositionChanged;
    }

    public MachineConfig Config { get; }
    public double JogSpeed => JogSpeeds[JogSpeedIndex];
    public StationMotionSettings CurrentMotionParams => SelectedStation switch
    {
        1 => Config.PcbPlacementMotion,
        2 => Config.BoltFastening.Motion,
        3 => Config.Inspection.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(SelectedStation)),
    };

    private IMotionService CurrentMotion => _motions[SelectedStation - 1];
    private int LaserChannel => LaserChannels[SelectedStation - 1];

    public void Activate()
    {
        UpdateReferenceStatus();
        UpdateOffsetText();
        RefreshPosition();
        StatusMessage = "Configuration loaded";
    }

    partial void OnSelectedStationChanged(int oldValue, int newValue)
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

        switch (SelectedStation)
        {
            case 1:
                Config.Calibration.PcbPlacementReference = position;
                PcbPlacementReferenceRecorded = true;
                break;
            case 2:
                Config.Calibration.BoltFasteningReference = position;
                BoltFasteningReferenceRecorded = true;
                break;
            case 3:
                Config.Calibration.InspectionReference = position;
                InspectionReferenceRecorded = true;
                break;
        }

        OnPropertyChanged(nameof(Config));
        StatusMessage = $"Station {SelectedStation} reference recorded";
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
        PcbPlacementReferenceRecorded
        && BoltFasteningReferenceRecorded
        && InspectionReferenceRecorded;

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
        _motions[0].PositionChanged -= OnPcbPlacementPositionChanged;
        _motions[1].PositionChanged -= OnBoltFasteningPositionChanged;
        _motions[2].PositionChanged -= OnInspectionPositionChanged;
    }

    private void UpdateReferenceStatus()
    {
        PcbPlacementReferenceRecorded = IsSet(Config.Calibration.PcbPlacementReference);
        BoltFasteningReferenceRecorded = IsSet(Config.Calibration.BoltFasteningReference);
        InspectionReferenceRecorded = IsSet(Config.Calibration.InspectionReference);
    }

    private static bool IsSet(AxisPos position) =>
        position.X != 0 || position.Y != 0 || position.Z != 0;

    private void UpdateOffsetText() =>
        OffsetResultText =
            $"Inspection → PCB: dX={Config.Calibration.InspectionToPcbPlacementOffset.X:F3} "
            + $"dY={Config.Calibration.InspectionToPcbPlacementOffset.Y:F3}  "
            + $"Inspection → Bolt: dX={Config.Calibration.InspectionToBoltFasteningOffset.X:F3} "
            + $"dY={Config.Calibration.InspectionToBoltFasteningOffset.Y:F3}";

    private void RefreshPosition()
    {
        var current = CurrentMotion.GetPosition();
        CurrentX = current.X;
        CurrentY = current.Y;
        CurrentZ = current.Z;
    }

    private void OnPcbPlacementPositionChanged(double x, double y, double z) =>
        ApplyPosition(1, x, y, z);

    private void OnBoltFasteningPositionChanged(double x, double y, double z) =>
        ApplyPosition(2, x, y, z);

    private void OnInspectionPositionChanged(double x, double y, double z) =>
        ApplyPosition(3, x, y, z);

    private void ApplyPosition(int station, double x, double y, double z) =>
        RunOnUi(() =>
        {
            if (SelectedStation == station)
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
