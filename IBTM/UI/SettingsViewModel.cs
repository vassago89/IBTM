using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MachineStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMotionSettings))]
    [NotifyPropertyChangedFor(nameof(CurrentMotionHardwareSettings))]
    [NotifyPropertyChangedFor(nameof(CurrentMotionHasZ))]
    private MotionGroup _selectedMotionGroup = MotionGroup.PcbSupply;

    public SettingsViewModel(
        MachineStore store,
        MachineSettings settings)
    {
        _store = store;
        Settings = settings;
        ControlDrivers = Enum.GetValues<ControlDriver>();
        CameraDrivers = Enum.GetValues<CameraDriver>();
        BoltDrivers = Enum.GetValues<BoltDriver>();
        var hardware = settings.HardwareSections;
        InputMappings = hardware
            .OfType<InputHardwareSettings>()
            .SelectMany(section => section.Inputs.Select(mapping =>
                CreateInputRow(section, mapping.Key, mapping.Value)))
            .ToArray();
        OutputMappings = hardware
            .OfType<IoHardwareSettings>()
            .SelectMany(section => section.Outputs.Select(mapping =>
                CreateOutputRow(section, mapping.Key, mapping.Value)))
            .ToArray();
        AxisMappings = hardware
            .OfType<MotionHardwareSettings>()
            .SelectMany(section => section.Axes.Select(mapping =>
                CreateAxisRow(section, mapping.Key, mapping.Value)))
            .ToArray();
        FeedbackMappings = hardware
            .OfType<IoHardwareSettings>()
            .SelectMany(section => section.Outputs
                .Where(mapping => mapping.Value.Feedback is not null)
                .Select(mapping => new KeyValuePair<OutputIo, OutputFeedback>(
                    mapping.Key,
                    mapping.Value.Feedback!)))
            .ToArray();
    }

    public MachineSettings Settings { get; }
    public ControlDriver[] ControlDrivers { get; }
    public CameraDriver[] CameraDrivers { get; }
    public BoltDriver[] BoltDrivers { get; }
    public AxisDirection[] AxisDirections { get; } =
        Enum.GetValues<AxisDirection>();
    public HardwareMappingRow[] InputMappings { get; }
    public HardwareMappingRow[] OutputMappings { get; }
    public HardwareMappingRow[] AxisMappings { get; }
    public KeyValuePair<OutputIo, OutputFeedback>[] FeedbackMappings { get; }
    public InputIo[] InputSignals { get; } = Enum.GetValues<InputIo>();
    public MotionGroup[] MotionGroups { get; } =
        Enum.GetValues<MotionGroup>();
    public MotionSettings CurrentMotionSettings => SelectedMotionGroup switch
    {
        MotionGroup.PcbSupply => Settings.PcbSupply.Motion,
        MotionGroup.PcbPlacementHandler => Settings.PcbPlacementHandler.Motion,
        MotionGroup.BoltFastening => Settings.BoltFastening.Motion,
        MotionGroup.InspectionGantry => Settings.InspectionGantry.Motion,
        _ => throw new ArgumentOutOfRangeException(
            nameof(SelectedMotionGroup)),
    };
    public MotionHardwareSettings CurrentMotionHardwareSettings =>
        SelectedMotionGroup switch
        {
            MotionGroup.PcbSupply => Settings.PcbSupplyHardware,
            MotionGroup.PcbPlacementHandler => Settings.PcbPlacementHandlerHardware,
            MotionGroup.BoltFastening => Settings.BoltFasteningHardware,
            MotionGroup.InspectionGantry => Settings.InspectionGantryHardware,
            _ => throw new ArgumentOutOfRangeException(
                nameof(SelectedMotionGroup)),
        };
    public bool CurrentMotionHasZ =>
        SelectedMotionGroup != MotionGroup.InspectionGantry;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        ApplyHardwareMappings();
        await _store.SaveSettingsAsync(Settings);
    }

    private void ApplyHardwareMappings()
    {
        foreach (var row in InputMappings
                     .Concat(OutputMappings)
                     .Concat(AxisMappings))
        {
            row.Apply();
        }
    }

    private static HardwareMappingRow CreateInputRow(
        InputHardwareSettings section,
        InputIo input,
        int number) =>
        new(
            section.Area,
            input,
            number,
            row => section.Inputs[input] = row.Number);

    private static HardwareMappingRow CreateOutputRow(
        IoHardwareSettings section,
        OutputIo output,
        OutputHardware hardware)
    {
        var row = new HardwareMappingRow(
            section.Area,
            output,
            hardware.Number,
            changed =>
            {
                hardware.Number = changed.Number;
                hardware.OffNumber = changed.OffNumber;
            });
        row.OffNumber = hardware.OffNumber;

        return row;
    }

    private static HardwareMappingRow CreateAxisRow(
        MotionHardwareSettings section,
        MachineAxis axis,
        AxisHardware hardware) =>
        new(
            section.Area,
            axis,
            hardware.Number,
            row =>
            {
                hardware.Number = row.Number;
                hardware.Direction = row.Direction;
                hardware.Minimum = row.Minimum;
                hardware.Maximum = row.Maximum;
            },
            hardware.Direction,
            hardware.Minimum,
            hardware.Maximum);
}
