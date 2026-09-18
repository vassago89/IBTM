using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
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
        OperationCancellation operations,
        ILightController light,
        ApplicationLog log)
    {
        _state = state;
        _store = store;
        _operations = operations;
        _light = light;
        _log = log;
        _lightTestChannel = settings.Lighting.InspectionChannel;
        ActiveLightConnection = settings.Drivers.Light == LightDriver.Virtual
            ? "Virtual"
            : $"{settings.Lighting.Connection} · {settings.Lighting.BaudRate} baud";
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
        BoltDrivers = Enum.GetValues<BoltDriver>();
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
        get
        {
            return Settings.Drivers.Bolt;
        }
        set
        {
            if (Settings.Drivers.Bolt == value)
                return;
            Settings.Drivers.Bolt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowIoBoltSettings));
            OnPropertyChanged(nameof(ShowAdcBoltSettings));
        }
    }

    public bool ShowIoBoltSettings
    {
        get
        {
            return SelectedBoltDriver == BoltDriver.Io;
        }
    }

    public bool ShowAdcBoltSettings
    {
        get
        {
            return SelectedBoltDriver != BoltDriver.Io;
        }
    }

    public bool IsVirtualDevelopment
    {
        get
        {
            return DevelopmentProfile.IsEnabled;
        }
    }

    public bool CanChangeDrivers
    {
        get
        {
            return CanEditSettings && !IsVirtualDevelopment;
        }
    }

    public ControlDriver[] ControlDrivers { get; }
    public CameraDriver[] CameraDrivers { get; }
    public BoltDriver[] BoltDrivers { get; }
    public LightDriver[] LightDrivers { get; } = Enum.GetValues<LightDriver>();
    public Parity[] LightParities { get; } = Enum.GetValues<Parity>();
    public StopBits[] LightStopBits { get; } = [StopBits.One, StopBits.OnePointFive, StopBits.Two];
    public int[] LightDataBits { get; } = [5, 6, 7, 8];

    public bool IsVirtualCamera
    {
        get
        {
            return _virtualCamera is not null;
        }
    }

    public HardwareMappingRow[] InputMappings { get; }
    public HardwareMappingRow[] OutputMappings { get; }
    public HardwareMappingRow[] AxisMappings { get; }
    public ICollectionView InputMappingView { get; }
    public ICollectionView OutputMappingView { get; }
    public MotionGroup[] MotionGroups { get; }

    public MotionSettings CurrentMotionSettings
    {
        get
        {
            return _motions[SelectedMotionGroup].Settings;
        }
    }

    public IEnumerable<HardwareMappingRow> CurrentAxisMappings
    {
        get
        {
            return AxisMappings.Where(row => row.Area == CurrentMotionHardwareSettings.Area);
        }
    }

    public MotionHardwareSettings CurrentMotionHardwareSettings
    {
        get
        {
            return _motions[SelectedMotionGroup].Hardware;
        }
    }

    public bool CurrentMotionHasZ
    {
        get
        {
            return CurrentMotionHardwareSettings.AxisSignals.ContainsKey(MotionAxis.Z);
        }
    }

    public bool CanEditSettings
    {
        get
        {
            return _state.SetupEditingEnabled && !SaveSettingsCommand.IsRunning;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private async Task SaveSettingsAsync()
    {
        DatabaseMessage = "Saving settings...";
        try
        {
            foreach (var row in InputMappings)
            {
                var hardware = (InputHardwareSettings)row.Hardware;
                hardware.Inputs[(InputIo)row.Signal] = row.Number;
            }

            await Settings.SaveAsync(_store);
            DatabaseMessage = "Settings saved. Restart to apply hardware changes.";
            Trace.TraceInformation(
                "Settings saved to {0}. Restart required for hardware changes.",
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
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanChangeDrivers));
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

    private bool CanChangeVirtualImage()
    {
        return IsVirtualCamera && CanEditSettings;
    }

    private bool CanClearVirtualImage()
    {
        return CanChangeVirtualImage() && VirtualImageName is not null;
    }

    public async Task ShutdownAsync()
    {
        Exception? failure = null;
        try
        {
            await CommandShutdown.StopAsync(
                null,
                SaveSettingsCommand,
                LoadVirtualImageCommand,
                TestLightCommand,
                OffTestLightCommand);
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
