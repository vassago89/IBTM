using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Data;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using IBTM.Virtual;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace IBTM.UI;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MachineState _state;
    private readonly MachineStore _store;
    private readonly OperationCancellation _operations;

    [ObservableProperty]
    public partial string? DatabaseMessage { get; set; }
    private readonly VirtualCamera? _virtualCamera;
    private readonly Dictionary<MotionGroup, (MotionSettings Settings, MotionHardwareSettings Hardware)> _motions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearVirtualImageCommand))]
    public partial string? VirtualImageName { get; set; }

    [ObservableProperty]
    public partial string? VirtualImageError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMotionSettings))]
    [NotifyPropertyChangedFor(nameof(CurrentMotionHardwareSettings))]
    [NotifyPropertyChangedFor(nameof(CurrentMotionHasZ))]
    [NotifyPropertyChangedFor(nameof(CurrentAxisMappings))]
    public partial MotionGroup SelectedMotionGroup { get; set; } = MotionGroup.PcbSupply;

    public SettingsViewModel(
        MachineSettings settings,
        MachineState state,
        ICamera camera,
        MachineStore store,
        OperationCancellation operations,
        ILightController light,
        ILogger<SettingsViewModel> log)
    {
        LightDrivers = Enum.GetValues<LightDriver>();

        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => IsSettingsEditAllowed);
        BrowsePcbResultsFolderCommand = new RelayCommand(BrowsePcbResultsFolder, () => IsSettingsEditAllowed);
        BrowseLogFolderCommand = new RelayCommand(BrowseLogFolder, () => IsSettingsEditAllowed);
        LoadVirtualImageCommand = new AsyncRelayCommand<string?>(LoadVirtualImageAsync, _ => IsChangeVirtualImageAllowed);
        ClearVirtualImageCommand = new RelayCommand(ClearVirtualImage, () => IsClearVirtualImageAllowed);
        OffTestLightCommand = new AsyncRelayCommand(OffTestLightAsync, () => IsOffTestLightAllowed);
        TestLightCommand = new AsyncRelayCommand(TestLightAsync, () => IsTestLightAllowed);

        _state = state;
        _store = store;
        _operations = operations;
        _light = light;
        _log = log;
        LightTestChannel = settings.Lighting.InspectionChannel;
        ActiveLightConnection = settings.Drivers.Light == LightDriver.Virtual
            ? "Virtual"
            : settings.Lighting.Connection;
        TestLightCommand.PropertyChanged += OnLightCommandChanged;
        OffTestLightCommand.PropertyChanged += OnLightCommandChanged;
        SaveSettingsCommand.PropertyChanged += OnSaveSettingsCommandChanged;
        _virtualCamera = camera as VirtualCamera;
        Settings = settings;
        ActiveControlDriver = settings.Drivers.Control;
        ActiveCameraDriver = settings.Drivers.Camera;
        ActiveBoltDriver = settings.Drivers.Bolt;
        ActiveLightDriver = settings.Drivers.Light;
        _motions = settings.MotionSections.ToDictionary(section => section.Hardware.Group);
        MotionGroups = _motions.Keys.ToArray();
        ControlDrivers = Enum.GetValues<ControlDriver>();
        CameraDrivers = Enum.GetValues<CameraDriver>();
        BoltDrivers = [BoltDriver.Virtual, BoltDriver.HantasAdc];
        var hardware = settings.HardwareSections;
        InputMappings = hardware.OfType<InputHardwareSettings>()
            .SelectMany(
                section => section.Inputs.Select(
                    mapping => new HardwareMappingRow(section, mapping.Key) { Number = mapping.Value }))
            .ToArray();
        OutputMappings = hardware.OfType<IoHardwareSettings>()
            .SelectMany(
                section =>
                    section.Outputs.Select(
                        mapping =>
                            new HardwareMappingRow(section, mapping.Key) { Output = mapping.Value }))
            .ToArray();
        AxisMappings = hardware.OfType<MotionHardwareSettings>()
            .SelectMany(
                section =>
                    section.Axes.Select(
                        mapping =>
                            new HardwareMappingRow(section, mapping.Key) { Axis = mapping.Value }))
            .ToArray();
        InputMappingView = GroupMappings(InputMappings);
        OutputMappingView = GroupMappings(OutputMappings);
    }

    public MachineSettings Settings { get; }
    public ControlDriver ActiveControlDriver { get; }
    public CameraDriver ActiveCameraDriver { get; }
    public BoltDriver ActiveBoltDriver { get; }
    public LightDriver ActiveLightDriver { get; }

    public BoltDriver SelectedBoltDriver
    {
        get => Settings.Drivers.Bolt;
        set
        {
            if (Settings.Drivers.Bolt == value)
                return;
            Settings.Drivers.Bolt = value;
            OnPropertyChanged();
        }
    }

    public bool IsVirtualDevelopment => DevelopmentProfile.IsEnabled;

    public bool IsDriverChangeAllowed => IsSettingsEditAllowed && !IsVirtualDevelopment;

    public ControlDriver[] ControlDrivers { get; }
    public CameraDriver[] CameraDrivers { get; }
    public BoltDriver[] BoltDrivers { get; }
    public LightDriver[] LightDrivers { get; }

    public bool IsVirtualCamera => _virtualCamera is not null;

    public HardwareMappingRow[] InputMappings { get; }
    public HardwareMappingRow[] OutputMappings { get; }
    public HardwareMappingRow[] AxisMappings { get; }
    public ICollectionView InputMappingView { get; }
    public ICollectionView OutputMappingView { get; }
    public MotionGroup[] MotionGroups { get; }

    public MotionSettings CurrentMotionSettings => _motions[SelectedMotionGroup].Settings;

    public IEnumerable<HardwareMappingRow> CurrentAxisMappings => AxisMappings.Where(row => row.Area == CurrentMotionHardwareSettings.Area);

    public MotionHardwareSettings CurrentMotionHardwareSettings => _motions[SelectedMotionGroup].Hardware;

    public bool CurrentMotionHasZ => CurrentMotionHardwareSettings.AxisSignals.ContainsKey(MotionAxis.Z);

    public bool IsSettingsEditAllowed => _state.SetupEditingEnabled && !SaveSettingsCommand.IsRunning;

    public IAsyncRelayCommand SaveSettingsCommand { get; }

    public IRelayCommand BrowsePcbResultsFolderCommand { get; }

    public IRelayCommand BrowseLogFolderCommand { get; }

    public string LogDirectory
    {
        get => Settings.Logging.Directory;
        set
        {
            Settings.Logging.Directory = value;
            OnPropertyChanged();
        }
    }

    private void BrowseLogFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Log folder" };
        if (Directory.Exists(LogDirectory))
            dialog.InitialDirectory = LogDirectory;
        if (dialog.ShowDialog() == true)
            LogDirectory = dialog.FolderName;
    }

    public string PcbResultsDirectory
    {
        get => Settings.PcbHistory.Directory;
        set
        {
            Settings.PcbHistory.Directory = value;
            OnPropertyChanged();
        }
    }

    private void BrowsePcbResultsFolder()
    {
        var dialog = new OpenFolderDialog { Title = "PCB results folder" };
        if (Directory.Exists(PcbResultsDirectory))
            dialog.InitialDirectory = PcbResultsDirectory;
        if (dialog.ShowDialog() == true)
            PcbResultsDirectory = dialog.FolderName;
    }

    private async Task SaveSettingsAsync()
    {
        DatabaseMessage = "Saving settings...";
        try
        {
            if (string.IsNullOrWhiteSpace(LogDirectory) || !Path.IsPathFullyQualified(LogDirectory))
                throw new InvalidOperationException("Choose an absolute folder path for logs.");
            _ = Path.GetFullPath(LogDirectory);
            if (string.IsNullOrWhiteSpace(PcbResultsDirectory) || !Path.IsPathFullyQualified(PcbResultsDirectory))
                throw new InvalidOperationException("Choose an absolute folder path for PCB results.");
            Directory.CreateDirectory(PcbResultsDirectory);
            foreach (var row in InputMappings)
            {
                var hardware = (InputHardwareSettings)row.Hardware;
                hardware.Inputs[(InputIo)row.Signal] = row.Number;
            }

            await Settings.SaveAsync(_store);
            DatabaseMessage = "Settings saved. Restart to apply hardware and logging changes.";
            Trace.TraceInformation(
                "Settings saved to {0}. Restart required for hardware and logging changes.",
                _store.DatabaseFile);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Machine settings save failed. {0}", exception);
            DatabaseMessage = $"Settings not saved: {exception.GetBaseException().Message}";
        }
    }

    private void OnSaveSettingsCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            RefreshCommands();
    }

    public void RefreshCommands()
    {
        LoadVirtualImageCommand.NotifyCanExecuteChanged();
        ClearVirtualImageCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        TestLightCommand.NotifyCanExecuteChanged();
        OffTestLightCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsSettingsEditAllowed));
        BrowsePcbResultsFolderCommand.NotifyCanExecuteChanged();
        BrowseLogFolderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDriverChangeAllowed));
    }

    public IAsyncRelayCommand<string?> LoadVirtualImageCommand { get; }

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
            var image = await Task.Run(
                () =>
                {
                    using var stream = File.OpenRead(path);
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        stream,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    return InspectionPreview.CreateFrame(decoder.Frames[0]);
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsChangeVirtualImageAllowed)
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

    public IRelayCommand ClearVirtualImageCommand { get; }

    private void ClearVirtualImage()
    {
        _virtualCamera!.SourceImage = null;
        VirtualImageName = null;
        VirtualImageError = null;
    }

    private bool IsChangeVirtualImageAllowed => IsVirtualCamera && IsSettingsEditAllowed;

    private bool IsClearVirtualImageAllowed => IsChangeVirtualImageAllowed && VirtualImageName is not null;

    public async Task ShutdownAsync()
    {
        Exception? failure = null;
        try
        {
            await CommandShutdown.CancelAndWaitAsync(
                [SaveSettingsCommand, LoadVirtualImageCommand, TestLightCommand, OffTestLightCommand]);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (PendingLightOffChannel is { } channel)
        {
            var offFailure = await TurnTestLightOffAsync(channel);
            if (offFailure is not null)
                failure = failure is null ? offFailure : new AggregateException(failure, offFailure);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static ICollectionView GroupMappings(IEnumerable<HardwareMappingRow> mappings)
    {
        var view = CollectionViewSource.GetDefaultView(mappings);
        view.SortDescriptions.Add(
            new(nameof(HardwareMappingRow.Area), System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(
            new(nameof(HardwareMappingRow.Section), System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(
            new(nameof(HardwareMappingRow.Order), System.ComponentModel.ListSortDirection.Ascending));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HardwareMappingRow.Area)));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HardwareMappingRow.Section)));
        return view;
    }
}
