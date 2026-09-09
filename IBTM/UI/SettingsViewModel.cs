using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.AlphaMotion;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection.Training;
using IBTM.Storage;
using IBTM.Virtual;
using Microsoft.Win32;

namespace IBTM.UI;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MachineState _state;
    private readonly MachineStore _store;
    private readonly OperationCancellation _operations;

    [ObservableProperty]
    private string? _databaseMessage;
    private readonly VirtualCamera? _virtualCamera;
    private readonly Dictionary<MotionGroup, (MotionSettings Settings, MotionHardwareSettings Hardware)> _motions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearVirtualImageCommand))]
    private string? _virtualImageName;

    [ObservableProperty]
    private string? _virtualImageError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMotionSettings))]
    [NotifyPropertyChangedFor(nameof(CurrentMotionHardwareSettings))]
    [NotifyPropertyChangedFor(nameof(CurrentMotionHasZ))]
    [NotifyPropertyChangedFor(nameof(CurrentAxisMappings))]
    private MotionGroup _selectedMotionGroup = MotionGroup.PcbSupply;

    public SettingsViewModel(
        MachineSettings settings,
        MachineState state,
        ICamera camera,
        MachineStore store,
        OperationCancellation operations)
    {
        _state = state;
        _store = store;
        _operations = operations;
        _virtualCamera = camera as VirtualCamera;
        Settings = settings;
        ActiveControlDriver = settings.Drivers.Control;
        ActiveCameraDriver = settings.Drivers.Camera;
        ActiveBoltDriver = settings.Drivers.Bolt;
        ActiveLightDriver = settings.Drivers.Light;
        ActiveInspectionAlgorithm = settings.Drivers.Inspection;
        _motions = settings.MotionSections.ToDictionary(section => section.Hardware.Group);
        MotionGroups = _motions.Keys.ToArray();
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
        InputMappingView = GroupMappings(InputMappings);
        OutputMappingView = GroupMappings(OutputMappings);
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
    public ControlDriver ActiveControlDriver { get; }
    public CameraDriver ActiveCameraDriver { get; }
    public BoltDriver ActiveBoltDriver { get; }
    public LightDriver ActiveLightDriver { get; }
    public InspectionAlgorithm ActiveInspectionAlgorithm { get; }
    public bool IsVirtualDevelopment => DevelopmentProfile.IsEnabled;
    public bool CanChangeDrivers => CanEditSettings && !IsVirtualDevelopment;
    public ControlDriver[] ControlDrivers { get; }
    public CameraDriver[] CameraDrivers { get; }
    public BoltDriver[] BoltDrivers { get; }
    public LightDriver[] LightDrivers { get; } = Enum.GetValues<LightDriver>();
    public Parity[] LightParities { get; } = Enum.GetValues<Parity>();
    public StopBits[] LightStopBits { get; } = [StopBits.One, StopBits.OnePointFive, StopBits.Two];
    public int[] LightDataBits { get; } = [5, 6, 7, 8];
    public InspectionAlgorithm[] InspectionAlgorithms { get; } =
        Enum.GetValues<InspectionAlgorithm>();
    public bool IsVirtualCamera => _virtualCamera is not null;
    public AlphaMotionCommunicationSpeed[] AlphaMotionCommunicationSpeeds { get; } =
        Enum.GetValues<AlphaMotionCommunicationSpeed>();
    public HardwareMappingRow[] InputMappings { get; }
    public HardwareMappingRow[] OutputMappings { get; }
    public HardwareMappingRow[] AxisMappings { get; }
    public ICollectionView InputMappingView { get; }
    public ICollectionView OutputMappingView { get; }
    public KeyValuePair<OutputIo, OutputFeedback>[] FeedbackMappings { get; }
    public InputIo[] InputSignals { get; } = Enum.GetValues<InputIo>();
    public MotionGroup[] MotionGroups { get; }
    public MotionSettings CurrentMotionSettings => _motions[SelectedMotionGroup].Settings;
    public IEnumerable<HardwareMappingRow> CurrentAxisMappings =>
        AxisMappings.Where(row => row.Area == CurrentMotionHardwareSettings.Area);
    public MotionHardwareSettings CurrentMotionHardwareSettings =>
        _motions[SelectedMotionGroup].Hardware;
    public bool CurrentMotionHasZ =>
        CurrentMotionHardwareSettings.AxisSignals.ContainsKey(MotionAxis.Z);

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void SelectMotionParameterFile()
    {
        var dialog = new OpenFileDialog { Title = "Select AJIN Motion Parameters", Filter = "AJIN parameters|*.mot" };
        if (dialog.ShowDialog() != true) return;
        Settings.Ajin.MotionParameterFile = dialog.FileName;
        OnPropertyChanged(nameof(Settings));
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private async Task SaveSettingsAsync()
    {
        if (Settings.Drivers.Light == LightDriver.Movs
            && string.IsNullOrWhiteSpace(Settings.Lighting.Connection))
        {
            DatabaseMessage = "MOVS light COM port is required. Set Devices & Safety > Lighting > COM Port before saving.";
            Trace.TraceError("Settings not saved: {0}", DatabaseMessage);
            return;
        }
        using var operation = _operations.Link();
        ApplyHardwareMappings();
        await Settings.SaveAsync(_store, operation.Token);
        DatabaseMessage = "Settings saved. Restart to apply driver, connection, pulse length and mapping changes.";
        Trace.TraceInformation("Settings saved to {0}. Restart required for hardware changes.", _store.DatabaseFile);
        _state.Refresh();
    }

    public bool CanEditSettings => _state.ManualMode && !_state.IsRunning;

    public void RefreshCommands()
    {
        LoadVirtualImageCommand.NotifyCanExecuteChanged();
        ClearVirtualImageCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        SelectMotionParameterFileCommand.NotifyCanExecuteChanged();
        BackupDatabaseCommand.NotifyCanExecuteChanged();
        RestoreDatabaseCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanChangeDrivers));
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private async Task BackupDatabaseAsync()
    {
        using var operation = _operations.Link();
        var dialog = new SaveFileDialog { Title = "Back Up Machine Settings and Recipes",
            Filter = "SQLite database|*.db", FileName = $"IBTM-Machine-{DateTime.Now:yyyyMMdd-HHmmss}.db" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await Task.Run(() => _store.Backup(dialog.FileName), operation.Token);
            DatabaseMessage = "Saved settings, recipes and carrier images backed up. Unsaved edits, AJIN .mot files and training data are not included.";
            Trace.TraceInformation("Machine database backed up to {0}.", dialog.FileName);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Trace.TraceError("Machine database backup failed. {0}", exception);
            DatabaseMessage = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private async Task RestoreDatabaseAsync()
    {
        using var operation = _operations.Link();
        var dialog = new OpenFileDialog { Title = "Restore Machine Settings and Recipes", Filter = "SQLite database|*.db" };
        if (dialog.ShowDialog() != true || MessageBox.Show(
            "Restore the selected machine database and close IBTM? Unsaved edits will be discarded. Training data is unchanged. The previous machine database is retained.",
            "Restore Machine Database", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            await Task.Run(() => _store.PrepareRestore(dialog.FileName), operation.Token);
            DatabaseMessage = "Restore prepared. The selected database will be applied on the next start.";
            Trace.TraceInformation("Machine database restore prepared from {0}.", dialog.FileName);
            _ = Application.Current.Dispatcher.BeginInvoke(() => Application.Current.MainWindow.Close());
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Trace.TraceError("Machine database restore failed. {0}", exception);
            DatabaseMessage = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeVirtualImage))]
    private async Task LoadVirtualImageAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Virtual Camera Image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            path = dialog.FileName;
        }

        try
        {
            VirtualImageError = null;
            var image = await Task.Run(() => BoltTrainingImages.Decode(File.ReadAllBytes(path)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanChangeVirtualImage())
            {
                VirtualImageError = "Stop the machine before changing the camera image.";
                return;
            }
            _virtualCamera!.SourceImage = image;
            VirtualImageName = Path.GetFileName(path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Virtual camera image load failed. {0}", exception);
            VirtualImageError = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearVirtualImage))]
    private void ClearVirtualImage()
    {
        _virtualCamera!.SourceImage = null;
        VirtualImageName = null;
        VirtualImageError = null;
    }

    private bool CanChangeVirtualImage() => IsVirtualCamera && CanEditSettings;
    private bool CanClearVirtualImage() => CanChangeVirtualImage() && VirtualImageName is not null;

    public Task ShutdownAsync() => CommandShutdown.StopAsync(
        LoadVirtualImageCommand.Cancel, SaveSettingsCommand, LoadVirtualImageCommand,
        BackupDatabaseCommand, RestoreDatabaseCommand);

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
            section,
            input,
            number,
            row => section.Inputs[input] = row.Number);

    private static HardwareMappingRow CreateOutputRow(
        IoHardwareSettings section,
        OutputIo output,
        OutputHardware hardware)
    {
        var row = new HardwareMappingRow(
            section,
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
            section,
            axis,
            hardware.Number,
            row =>
            {
                hardware.Number = row.Number;
                hardware.Minimum = row.Minimum;
                hardware.Maximum = row.Maximum;
            },
            hardware.Minimum,
            hardware.Maximum);

    private static ICollectionView GroupMappings(
        IEnumerable<HardwareMappingRow> mappings)
    {
        var view = CollectionViewSource.GetDefaultView(mappings);
        view.SortDescriptions.Add(new(
            nameof(HardwareMappingRow.Area),
            System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new(
            nameof(HardwareMappingRow.Section),
            System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new(
            nameof(HardwareMappingRow.Order),
            System.ComponentModel.ListSortDirection.Ascending));
        view.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(HardwareMappingRow.Area)));
        view.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(HardwareMappingRow.Section)));
        return view;
    }
}
