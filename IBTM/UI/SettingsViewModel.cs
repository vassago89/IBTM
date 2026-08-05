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
    private readonly IReadOnlyDictionary<MotionGroup, MotionService> _motions;
    private readonly EquipmentState _state;
    private readonly IConveyorServo _conveyor;
    private readonly IIoService _io;
    private readonly MachineStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMotionSettings))]
    private MotionGroup _selectedMotionGroup = MotionGroup.PcbSupply;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(HomeAxisCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopHomingCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleServoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunConveyorCommand))]
    private bool _isHoming;

    public SettingsViewModel(
        [FromKeyedServices(MotionGroup.PcbSupply)] MotionService pcbSupplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacement)] MotionService pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] MotionService boltFasteningMotion,
        [FromKeyedServices(MotionGroup.Inspection)] MotionService inspectionMotion,
        IConveyorServo conveyor,
        IIoService io,
        EquipmentState state,
        MachineStore store,
        MachineSettings settings)
    {
        _motions = new Dictionary<MotionGroup, MotionService>
        {
            [MotionGroup.PcbSupply] = pcbSupplyMotion,
            [MotionGroup.PcbPlacement] = pcbPlacementMotion,
            [MotionGroup.BoltFastening] = boltFasteningMotion,
            [MotionGroup.Inspection] = inspectionMotion,
        };
        _conveyor = conveyor;
        _io = io;
        _state = state;
        _store = store;
        Settings = settings;
        ControlDrivers = Enum.GetValues<ControlDriver>();
        CameraDrivers = Enum.GetValues<CameraDriver>();
        InputMappings = Enum.GetValues<InputIo>()
            .Select(input =>
                new HardwareMappingRow(
                    input,
                    settings.Hardware.Inputs[input]))
            .ToArray();
        OutputMappings = Enum.GetValues<OutputIo>()
            .Select(output =>
                new HardwareMappingRow(
                    output,
                    settings.Hardware.Outputs[output]))
            .ToArray();
        AxisMappings = Enum.GetValues<MachineAxis>()
            .Select(axis => new HardwareMappingRow(
                axis,
                settings.Hardware.Axes[axis],
                settings.Hardware.AxisDirections[axis],
                settings.Hardware.AxisMinimums[axis],
                settings.Hardware.AxisMaximums[axis]))
            .ToArray();
        FeedbackMappings = settings.Hardware.OutputFeedbacks.ToArray();
        state.Changed += OnEquipmentStateChanged;
        io.InputChanged += OnInputChanged;
        state.Refresh();
    }

    public MachineSettings Settings { get; }
    public ControlDriver[] ControlDrivers { get; }
    public CameraDriver[] CameraDrivers { get; }
    public AxisDirection[] AxisDirections { get; } =
        Enum.GetValues<AxisDirection>();
    public HardwareMappingRow[] InputMappings { get; }
    public HardwareMappingRow[] OutputMappings { get; }
    public HardwareMappingRow[] AxisMappings { get; }
    public KeyValuePair<OutputIo, OutputFeedback>[] FeedbackMappings { get; }
    public InputIo[] InputSignals { get; } = Enum.GetValues<InputIo>();
    public MotionGroup[] MotionGroups { get; } =
        Enum.GetValues<MotionGroup>();
    public bool MachineReady => _state.Ready;
    public bool SafetyReady => _state.SafetyReady;
    public MotionSettings CurrentMotionSettings => SelectedMotionGroup switch
    {
        MotionGroup.PcbSupply => Settings.PcbSupply.Motion,
        MotionGroup.PcbPlacement => Settings.PcbPlacement.Motion,
        MotionGroup.BoltFastening => Settings.BoltFastening.Motion,
        MotionGroup.Inspection => Settings.Inspection.Motion,
        _ => throw new ArgumentOutOfRangeException(
            nameof(SelectedMotionGroup)),
    };

    public void Activate()
    {
        _state.Refresh();
        RefreshHardwareState();
        StatusMessage = !SafetyReady
            ? "Safety interlock is open"
            : MachineReady
                ? "Machine ready"
                : "Home required";
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            ApplyHardwareMappings();
            await _store.SaveSettingsAsync(Settings);
            StatusMessage =
                "Settings saved. Restart to apply hardware configuration";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Settings save failed: {exception.Message}";
        }
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
            Settings.Hardware.AxisMinimums[axis] = row.Minimum;
            Settings.Hardware.AxisMaximums[axis] = row.Maximum;
        }
    }
}
