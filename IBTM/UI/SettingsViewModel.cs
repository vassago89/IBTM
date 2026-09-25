using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
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
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace IBTM.UI;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MachineState _state;
    private readonly MachineStore _store;
    private readonly OperationCancellation _operations;
    private readonly VirtualCamera? _virtualCamera;
    private readonly Dictionary<MotionGroup, (MotionSettings Settings, MotionHardwareSettings Hardware)> _motions;
    private readonly ILightController _light;
    private readonly ILogger<SettingsViewModel> _log;

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

    [ObservableProperty]
    public partial string? DatabaseMessage { get; set; }

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

    public IEnumerable<HardwareMappingRow> CurrentAxisMappings => AxisMappings.Where(row => row.Hardware.Area == CurrentMotionHardwareSettings.Area);

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
            foreach (var (group, section) in _motions)
            {
                var hasZ = section.Hardware.AxisSignals.ContainsKey(MotionAxis.Z);
                if (section.Settings.GetValidationError(hasZ) is { } error)
                    throw new InvalidOperationException($"{group}: {error}");
            }
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
            _log.LogInformation(
                "Settings saved to {Database}. Restart required for hardware and logging changes.",
                _store.DatabaseFile);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "Machine settings save failed.");
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
            _log.LogError(exception, "Virtual camera image load failed.");
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
            new($"{nameof(HardwareMappingRow.Hardware)}.{nameof(HardwareSettings.Area)}", System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(
            new(nameof(HardwareMappingRow.Section), System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(
            new(nameof(HardwareMappingRow.Order), System.ComponentModel.ListSortDirection.Ascending));
        view.GroupDescriptions.Add(new PropertyGroupDescription($"{nameof(HardwareMappingRow.Hardware)}.{nameof(HardwareSettings.Area)}"));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HardwareMappingRow.Section)));
        return view;
    }

    [ObservableProperty]
    public partial int LightTestChannel { get; set; }

    [ObservableProperty]
    public partial int LightTestLevel { get; set; } = 80;

    [ObservableProperty]
    public partial bool LightTestOn { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestLightCommand), nameof(OffTestLightCommand))]
    public partial int? PendingLightOffChannel { get; set; }

    [ObservableProperty]
    public partial string LightTestMessage { get; set; } = "Test only: does not change recipe brightness.";

    public string ActiveLightConnection { get; }

    private bool IsTestLightAllowed
    {
        get
        {
            return IsSettingsEditAllowed
                && PendingLightOffChannel is null
                && !OffTestLightCommand.IsRunning;
        }
    }

    private bool IsOffTestLightAllowed
    {
        get
        {
            return TestLightCommand.IsRunning
                || PendingLightOffChannel is not null
                && !_operations.IsShuttingDown;
        }
    }

    private void OnLightCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning))
            return;
        TestLightCommand.NotifyCanExecuteChanged();
        OffTestLightCommand.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand OffTestLightCommand { get; }

    private async Task OffTestLightAsync()
    {
        switch (true)
        {
            case true when TestLightCommand.IsRunning:
                TestLightCommand.Cancel();
                if (TestLightCommand.ExecutionTask is { } test)
                    await test;
                return;
            case true when PendingLightOffChannel is { } channel:
                try
                {
                    using var operation = _operations.Link();
                    var failure = await TurnTestLightOffAsync(channel);
                    LightTestMessage = failure?.Message ?? $"OFF command sent · channel {channel}.";
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    RefreshCommands();
                }
                break;
        }
    }

    private async Task<Exception?> TurnTestLightOffAsync(int channel)
    {
        PendingLightOffChannel = channel;
        try
        {
            // Reconnect if needed, but never send ON/brightness during an OFF retry.
            await Task.Run(
                () =>
                {
                    _light.Initialize();
                    _light.TurnOff(channel);
                });
            _log.LogInformation("{Message}", $"Lighting test OFF command sent: channel={channel}.");
            PendingLightOffChannel = null;
            return null;
        }
        catch (Exception exception)
        {
            var failure = new InvalidOperationException(
                $"OFF failed on channel {channel}; light state is unknown. Press OFF to retry. {exception.Message}",
                exception);
            _log.LogError(exception, "Lighting test cleanup failed; light may still be ON.");
            return failure;
        }
        finally
        {
            LightTestOn = false;
        }
    }

    public IAsyncRelayCommand TestLightCommand { get; }

    private async Task TestLightAsync(CancellationToken cancellationToken)
    {
        // MOVS commands have a single channel digit; zero addresses all channels.
        if (LightTestChannel is < 1 or > 9 || LightTestLevel is < 0 or > 255)
        {
            LightTestMessage = "Use a single channel (1–9) and brightness 0–255.";
            return;
        }

        var channel = LightTestChannel;
        var level = LightTestLevel;
        OperationCancellation.Operation? operation;
        try
        {
            operation = _operations.TryBegin(cancellationToken);
            if (operation is null)
            {
                LightTestMessage = "Wait for the current machine operation to finish.";
                return;
            }
        }
        catch (OperationCanceledException)
        {
            LightTestMessage = "Lighting test cancelled.";
            RefreshCommands();
            return;
        }

        using (operation)
        {
            void StopWhenUnavailable()
            {
                if (!_state.ManualMode || _operations.IsShuttingDown)
                    operation.Cancel();
            }

            _state.Changed += StopWhenUnavailable;
            var initialized = false;
            Exception? failure = null;
            try
            {
                StopWhenUnavailable();
                LightTestMessage = $"Connecting: {ActiveLightConnection}…";
                _log.LogInformation(
                    "{Message}",
                    $"Lighting test started: driver={ActiveLightDriver}, connection={ActiveLightConnection}, channel={channel}, level={level}.");
                await Task.Run(
                    () =>
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        _light.Initialize();
                        initialized = true;
                        operation.Token.ThrowIfCancellationRequested();
                        _light.SetLevel(channel, level);
                        operation.Token.ThrowIfCancellationRequested();
                        _light.TurnOn(channel);
                    },
                    operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                PendingLightOffChannel = channel;
                LightTestOn = true;
                LightTestMessage = $"ON command sent · channel {channel}, level {level}. Press OFF to finish.";
                _log.LogInformation("{Message}", $"Lighting test ON command sent: channel={channel}, level={level}.");
                // Keep the operation owned while illuminated, including OFF cleanup.
                // This blocks automatic/motion admission and lets STOP cancel the test.
                await Task.Delay(Timeout.Infinite, operation.Token);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failure = exception;
                _log.LogError(exception, "Lighting test failed.");
            }
            finally
            {
                _state.Changed -= StopWhenUnavailable;
                if (initialized)
                {
                    // Required cleanup and retries target the captured channel, not edited settings.
                    var offFailure = await TurnTestLightOffAsync(channel);
                    failure = offFailure ?? failure;
                }

                LightTestOn = false;
                LightTestMessage = failure?.Message ?? (initialized
                    ? $"OFF command sent · channel {channel}."
                    : "Lighting test cancelled.");
            }
        }

        RefreshCommands();
    }
}
