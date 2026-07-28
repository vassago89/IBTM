using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<EquipmentUnit, MotionService> _motions;
    private readonly IConveyorServo _conveyor;
    private readonly IIoService _io;
    private readonly MachineStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMotionParams))]
    private EquipmentUnit _selectedUnit = EquipmentUnit.PcbSupply;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public SettingsViewModel(
        [FromKeyedServices(EquipmentUnit.PcbSupply)] MotionService pcbSupplyMotion,
        [FromKeyedServices(EquipmentUnit.PcbPlacement)] MotionService pcbPlacementMotion,
        [FromKeyedServices(EquipmentUnit.BoltFastening)] MotionService boltFasteningMotion,
        [FromKeyedServices(EquipmentUnit.Inspection)] MotionService inspectionMotion,
        IConveyorServo conveyor,
        IIoService io,
        MachineStore store,
        MachineSettings settings)
    {
        _motions = new Dictionary<EquipmentUnit, MotionService>
        {
            [EquipmentUnit.PcbSupply] = pcbSupplyMotion,
            [EquipmentUnit.PcbPlacement] = pcbPlacementMotion,
            [EquipmentUnit.BoltFastening] = boltFasteningMotion,
            [EquipmentUnit.Inspection] = inspectionMotion,
        };
        _conveyor = conveyor;
        _io = io;
        _store = store;
        Settings = settings;
        HardwareDrivers = Enum.GetValues<HardwareDriver>();
        CameraDrivers = Enum.GetValues<CameraDriver>();
        InputMappings = Enum.GetValues<InputIo>()
            .Select(input => new HardwareMappingRow(input, settings.Hardware.Inputs[input]))
            .ToArray();
        OutputMappings = Enum.GetValues<OutputIo>()
            .Select(output => new HardwareMappingRow(output, settings.Hardware.Outputs[output]))
            .ToArray();
        AxisMappings = Enum.GetValues<MachineAxis>()
            .Select(axis => new HardwareMappingRow(
                axis,
                settings.Hardware.Axes[axis],
                settings.Hardware.AxisDirections[axis]))
            .ToArray();
        FeedbackMappings = settings.Hardware.OutputFeedbacks
            .Select(mapping => new OutputFeedbackRow(mapping.Key, mapping.Value))
            .ToArray();
    }

    public MachineSettings Settings { get; }
    public HardwareDriver[] HardwareDrivers { get; }
    public CameraDriver[] CameraDrivers { get; }
    public AxisDirection[] AxisDirections { get; } = Enum.GetValues<AxisDirection>();
    public HardwareMappingRow[] InputMappings { get; }
    public HardwareMappingRow[] OutputMappings { get; }
    public HardwareMappingRow[] AxisMappings { get; }
    public OutputFeedbackRow[] FeedbackMappings { get; }
    public EquipmentUnit[] Units { get; } = Enum.GetValues<EquipmentUnit>();
    public StationMotionSettings CurrentMotionParams => SelectedUnit switch
    {
        EquipmentUnit.PcbSupply => Settings.PcbSupply.Motion,
        EquipmentUnit.PcbPlacement => Settings.PcbPlacement.Motion,
        EquipmentUnit.BoltFastening => Settings.BoltFastening.Motion,
        EquipmentUnit.Inspection => Settings.Inspection.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(SelectedUnit)),
    };

    public void Activate()
    {
        RefreshHardware();
        StatusMessage = "Settings loaded";
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            ApplyHardwareMappings();
            await _store.SaveSettingsAsync(Settings);
            StatusMessage = "Settings saved. Restart to apply hardware configuration";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Settings save failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private void RunConveyor()
    {
        _conveyor.Run(Settings.Conveyor.Velocity);
        StatusMessage = $"Conveyor running at {Settings.Conveyor.Velocity:F1} mm/s";
    }

    [RelayCommand]
    private void StopConveyor()
    {
        _conveyor.Stop();
        StatusMessage = "Conveyor stopped";
    }

    [RelayCommand]
    private void RefreshHardware()
    {
        foreach (var row in InputMappings)
        {
            row.State = _io.GetInput((InputIo)row.Signal) ? "ON" : "OFF";
        }

        foreach (var row in OutputMappings)
        {
            row.State = _io.GetOutput((OutputIo)row.Signal) ? "ON" : "OFF";
        }

        foreach (var row in AxisMappings)
        {
            row.State = FormatAxisState(GetAxisState((MachineAxis)row.Signal));
        }

        StatusMessage = "Hardware state refreshed";
    }

    [RelayCommand]
    private async Task ToggleOutputAsync(HardwareMappingRow row)
    {
        var output = (OutputIo)row.Signal;
        var value = !_io.GetOutput(output);
        try
        {
            if (Settings.Hardware.OutputFeedbacks.ContainsKey(output))
            {
                await _io.SetOutputAndWaitAsync(output, value);
            }
            else
            {
                _io.SetOutput(output, value);
            }

            RefreshHardware();
            StatusMessage = $"{output} {(value ? "on" : "off")}";
        }
        catch (IoFeedbackTimeoutException exception)
        {
            RefreshHardware();
            StatusMessage = $"Alarm: {exception.Message}";
        }
    }

    [RelayCommand]
    private void ToggleServo(HardwareMappingRow row)
    {
        var machineAxis = (MachineAxis)row.Signal;
        var turnOn = !GetAxisState(machineAxis).ServoOn;

        if (machineAxis == MachineAxis.Conveyor)
        {
            _conveyor.SetServo(turnOn);
        }
        else
        {
            var (motion, axis) = GetMotionAxis(machineAxis);
            motion.SetServo(axis, turnOn);
        }

        row.State = FormatAxisState(GetAxisState(machineAxis));
        StatusMessage = $"{machineAxis} servo {(turnOn ? "on" : "off")}";
    }

    [RelayCommand]
    private async Task HomeAxisAsync(HardwareMappingRow row)
    {
        var machineAxis = (MachineAxis)row.Signal;
        var (motion, axis) = GetMotionAxis(machineAxis);
        var velocity = axis == MotionAxis.Z
            ? Settings.Home.SpeedZ
            : Settings.Home.HorizontalSpeed;

        StatusMessage = $"Homing {machineAxis}";
        await motion.HomeAsync(axis, velocity);
        RefreshHardware();
        StatusMessage = $"{machineAxis} homed";
    }

    [RelayCommand]
    private void ResetHardwareAlarm()
    {
        foreach (var motion in _motions.Values)
        {
            motion.ResetAlarm();
        }

        _conveyor.ResetAlarm();
        RefreshHardware();
        StatusMessage = "Motion alarms reset";
    }

    public void Deactivate()
    {
        foreach (var motion in _motions.Values)
        {
            motion.Stop();
        }

        _conveyor.Stop();
    }

    private void ApplyHardwareMappings()
    {
        foreach (var row in InputMappings)
        {
            Settings.Hardware.Inputs[(InputIo)row.Signal] = row.Number;
        }

        foreach (var row in OutputMappings)
        {
            Settings.Hardware.Outputs[(OutputIo)row.Signal] = row.Number;
        }

        foreach (var row in AxisMappings)
        {
            var axis = (MachineAxis)row.Signal;
            Settings.Hardware.Axes[axis] = row.Number;
            Settings.Hardware.AxisDirections[axis] = row.Direction;
        }
    }

    private AxisState GetAxisState(MachineAxis axis)
    {
        if (axis == MachineAxis.Conveyor)
        {
            return _conveyor.GetAxisState();
        }

        var (motion, motionAxis) = GetMotionAxis(axis);
        return motion.GetAxisState(motionAxis);
    }

    private (MotionService Motion, MotionAxis Axis) GetMotionAxis(MachineAxis axis) =>
        axis switch
        {
            MachineAxis.PcbSupplyX => (_motions[EquipmentUnit.PcbSupply], MotionAxis.X),
            MachineAxis.PcbSupplyZ => (_motions[EquipmentUnit.PcbSupply], MotionAxis.Z),
            MachineAxis.PcbPlacementX => (_motions[EquipmentUnit.PcbPlacement], MotionAxis.X),
            MachineAxis.PcbPlacementY => (_motions[EquipmentUnit.PcbPlacement], MotionAxis.Y),
            MachineAxis.PcbPlacementZ => (_motions[EquipmentUnit.PcbPlacement], MotionAxis.Z),
            MachineAxis.BoltFasteningX => (_motions[EquipmentUnit.BoltFastening], MotionAxis.X),
            MachineAxis.BoltFasteningY => (_motions[EquipmentUnit.BoltFastening], MotionAxis.Y),
            MachineAxis.BoltFasteningZ => (_motions[EquipmentUnit.BoltFastening], MotionAxis.Z),
            MachineAxis.InspectionX => (_motions[EquipmentUnit.Inspection], MotionAxis.X),
            MachineAxis.InspectionY => (_motions[EquipmentUnit.Inspection], MotionAxis.Y),
            MachineAxis.InspectionZ => (_motions[EquipmentUnit.Inspection], MotionAxis.Z),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

    private static string FormatAxisState(AxisState state)
    {
        var values = new List<string>
        {
            state.ServoOn ? "SERVO ON" : "SERVO OFF",
            state.Homed ? "HOMED" : "NOT HOMED",
            state.InPosition ? "IN POSITION" : "MOVING",
        };

        if (state.HomeSensor)
        {
            values.Add("HOME SENSOR");
        }

        if (state.Alarm)
        {
            values.Add("ALARM");
        }

        if (state.Emergency)
        {
            values.Add("EMERGENCY");
        }

        if (state.PositiveLimit)
        {
            values.Add("+LIMIT");
        }

        if (state.NegativeLimit)
        {
            values.Add("-LIMIT");
        }

        return string.Join(" | ", values);
    }
}
