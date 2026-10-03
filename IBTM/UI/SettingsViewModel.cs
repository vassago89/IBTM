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
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace IBTM.UI;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly MachineStore _store;
    private readonly OperationCancellation _operations;
    private readonly Dictionary<MotionGroup, (MotionSettings Settings, MotionHardwareSettings Hardware)> _motions;
    private readonly ILightController _light;
    private readonly ILogger<SettingsViewModel> _log;

    public SettingsViewModel(
        MachineSettings settings,
        MachineState state,
        MachineController machine,
        MachineStore store,
        OperationCancellation operations,
        ILightController light,
        ILogger<SettingsViewModel> log)
    {
        Languages = Enum.GetValues<UiLanguage>();

        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        BrowsePcbResultsFolderCommand = new RelayCommand(BrowsePcbResultsFolder);
        BrowseLogFolderCommand = new RelayCommand(BrowseLogFolder);
        OffTestLightCommand = new AsyncRelayCommand(OffTestLightAsync);
        TestLightCommand = new AsyncRelayCommand(TestLightAsync);

        _state = state;
        _machine = machine;
        _store = store;
        _operations = operations;
        _light = light;
        _log = log;
        LightTestChannel = settings.Lighting.InspectionChannel;
        ActiveLightConnection = settings.Lighting.Connection;
        TestLightCommand.PropertyChanged += OnLightCommandChanged;
        OffTestLightCommand.PropertyChanged += OnLightCommandChanged;
        SaveSettingsCommand.PropertyChanged += OnSaveSettingsCommandChanged;
        Settings = settings;
        _motions = settings.MotionSections.ToDictionary(section => section.Hardware.Group);
        MotionGroups = _motions.Keys.ToArray();
        var hardware = settings.HardwareSections;
        var inputMappings = hardware.OfType<InputHardwareSettings>()
            .SelectMany(
                section => section.Inputs.Select(
                    mapping => new HardwareMappingRow(section, mapping.Key)))
            .ToArray();
        var outputMappings = hardware.OfType<IoHardwareSettings>()
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
        InputMappingView = GroupMappings(inputMappings);
        OutputMappingView = GroupMappings(outputMappings);
    }

    [ObservableProperty]
    public partial string? DatabaseMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMotionSettings))]
    [NotifyPropertyChangedFor(nameof(CurrentMotionHasZ))]
    [NotifyPropertyChangedFor(nameof(CurrentAxisMappings))]
    public partial MotionGroup SelectedMotionGroup { get; set; } = MotionGroup.PcbSupply;

    public MachineSettings Settings { get; }

    public UiLanguage[] Languages { get; }

    public HardwareMappingRow[] AxisMappings { get; }

    public ICollectionView InputMappingView { get; }

    public ICollectionView OutputMappingView { get; }

    public MotionGroup[] MotionGroups { get; }

    public MotionSettings CurrentMotionSettings => _motions[SelectedMotionGroup].Settings;

    public IEnumerable<HardwareMappingRow> CurrentAxisMappings => AxisMappings.Where(row => row.Hardware.Area == CurrentMotionHardwareSettings.Area);

    private MotionHardwareSettings CurrentMotionHardwareSettings => _motions[SelectedMotionGroup].Hardware;

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
        if (!IsSettingsEditAllowed)
            return;
        var dialog = new OpenFolderDialog { Title = UiText.Get("Log folder") };
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
        if (!IsSettingsEditAllowed)
            return;
        var dialog = new OpenFolderDialog { Title = UiText.Get("PCB results folder") };
        if (Directory.Exists(PcbResultsDirectory))
            dialog.InitialDirectory = PcbResultsDirectory;
        if (dialog.ShowDialog() == true)
            PcbResultsDirectory = dialog.FolderName;
    }

    private async Task SaveSettingsAsync()
    {
        if (!IsSettingsEditAllowed)
            return;
        DatabaseMessage = UiText.Get("Saving settings...");
        try
        {
            string? validationError = null;
            foreach (var (group, section) in _motions)
            {
                var hasZ = section.Hardware.AxisSignals.ContainsKey(MotionAxis.Z);
                if (section.Settings.GetValidationError(hasZ) is { } error)
                {
                    validationError = $"{UiText.Get(group)}: {error}";
                    break;
                }
            }
            if (validationError is null)
            {
                if (Settings.Lighting.InspectionChannel is < 1 or > 9)
                    validationError = UiText.Get("Inspection light channel must be from 1 to 9.");
                else if (Settings.Hantas.FasteningTimeoutMilliseconds <= 0)
                    validationError = UiText.Get("Fastening timeout must be greater than 0 s.");
                else if (Settings.Hantas.ResponseTimeoutMilliseconds <= 0)
                    validationError = UiText.Get("ADC response timeout must be greater than 0 s.");
                else if (string.IsNullOrWhiteSpace(LogDirectory) || !Path.IsPathFullyQualified(LogDirectory))
                    validationError = UiText.Get("Choose an absolute folder path for logs.");
                else if (string.IsNullOrWhiteSpace(PcbResultsDirectory) || !Path.IsPathFullyQualified(PcbResultsDirectory))
                    validationError = UiText.Get("Choose an absolute folder path for PCB results.");
            }
            if (validationError is not null)
            {
                DatabaseMessage = UiText.Format($"Settings not saved: {validationError}");
                _log.LogWarning("Machine settings were not saved: {Reason}", validationError);
                return;
            }
            _ = Path.GetFullPath(LogDirectory);
            Directory.CreateDirectory(PcbResultsDirectory);
            await _store.SaveSettingsAsync(Settings.Sections);
            DatabaseMessage = UiText.Get("Settings saved.");
            _log.LogInformation(
                "Settings saved to {Database}. Restart required for hardware and logging changes.",
                _store.DatabaseFile);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "Machine settings save failed.");
            DatabaseMessage = UiText.Format($"Settings not saved: {exception.GetBaseException().Message}");
        }
    }

    private void OnSaveSettingsCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            RefreshCommands();
    }

    public void RefreshCommands()
    {
        OnPropertyChanged(nameof(IsTestLightAllowed));
        OnPropertyChanged(nameof(IsOffTestLightAllowed));
        OnPropertyChanged(nameof(IsSettingsEditAllowed));
    }

    public async Task ShutdownAsync()
    {
        Exception? failure = null;
        try
        {
            await CommandShutdown.CancelAndWaitAsync(
                [SaveSettingsCommand, TestLightCommand, OffTestLightCommand]);
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
    [NotifyPropertyChangedFor(nameof(IsTestLightAllowed), nameof(IsOffTestLightAllowed))]
    public partial int? PendingLightOffChannel { get; set; }

    [ObservableProperty]
    public partial string LightTestMessage { get; set; } = "";

    public string ActiveLightConnection { get; }

    public bool IsTestLightAllowed
    {
        get
        {
            return IsSettingsEditAllowed
                && _state.ManualMode
                && PendingLightOffChannel is null
                && !OffTestLightCommand.IsRunning;
        }
    }

    public bool IsOffTestLightAllowed
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
        OnPropertyChanged(nameof(IsTestLightAllowed));
        OnPropertyChanged(nameof(IsOffTestLightAllowed));
    }

    public IAsyncRelayCommand OffTestLightCommand { get; }

    private async Task OffTestLightAsync()
    {
        if (TestLightCommand.IsRunning)
        {
            TestLightCommand.Cancel();
            if (TestLightCommand.ExecutionTask is { } test)
                await test;
            return;
        }
        if (PendingLightOffChannel is not { } channel)
            return;
        try
        {
            using var operation = _operations.Link();
            var failure = await TurnTestLightOffAsync(channel);
            LightTestMessage = failure?.Message ?? UiText.Format($"OFF command sent · channel {channel}.");
        }
        catch (OperationCanceledException) when (_operations.IsShuttingDown)
        {
        }
        finally
        {
            RefreshCommands();
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
            _log.LogInformation("Lighting test OFF command sent: channel={Channel}.", channel);
            PendingLightOffChannel = null;
            return null;
        }
        catch (Exception exception)
        {
            var failure = new InvalidOperationException(
                UiText.Format($"OFF failed on channel {channel}; light state is unknown. Press OFF to retry. {exception.Message}"),
                exception);
            _log.LogError(exception, "Lighting test cleanup failed; light may still be ON.");
            return failure;
        }
    }

    public IAsyncRelayCommand TestLightCommand { get; }

    private async Task TestLightAsync(CancellationToken cancellationToken)
    {
        // MOVS commands have a single channel digit; zero addresses all channels.
        if (LightTestChannel is < 1 or > 9 || LightTestLevel is < 0 or > 255)
        {
            LightTestMessage = UiText.Get("Use a single channel (1–9) and brightness 0–255.");
            return;
        }

        var channel = LightTestChannel;
        var level = LightTestLevel;
        OperationCancellation.Operation? operation = null;
        var initialized = false;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginManualOperation(
                () => _state.ManualMode && !_operations.IsShuttingDown,
                cancellationToken);
            if (operation is null)
            {
                LightTestMessage = UiText.Get("Wait for the current machine operation to finish.");
                return;
            }

            LightTestMessage = UiText.Format($"Connecting: {ActiveLightConnection}…");
            _log.LogInformation(
                "Lighting test started: connection={Connection}, channel={Channel}, level={Level}.",
                ActiveLightConnection, channel, level);
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
            LightTestMessage = UiText.Format($"ON command sent · channel {channel}, level {level}.");
            _log.LogInformation("Lighting test ON command sent: channel={Channel}, level={Level}.", channel, level);
            // Keep the operation owned while illuminated, including OFF cleanup.
            // This blocks automatic/motion admission and lets STOP cancel the test.
            await Task.Delay(Timeout.Infinite, operation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            || operation?.IsCancellationRequested == true || _operations.IsShuttingDown)
        {
            LightTestMessage = UiText.Get("Lighting test cancelled.");
        }
        catch (Exception exception)
        {
            failure = exception;
            LightTestMessage = exception.Message;
            _log.LogError(exception, "Lighting test failed.");
        }
        finally
        {
            try
            {
                if (initialized)
                {
                    // Required cleanup and retries target the captured channel, not edited settings.
                    var offFailure = await TurnTestLightOffAsync(channel);
                    LightTestMessage = (offFailure ?? failure)?.Message
                        ?? UiText.Format($"OFF command sent · channel {channel}.");
                }
            }
            finally
            {
                operation?.Dispose();
                RefreshCommands();
            }
        }
    }
}
