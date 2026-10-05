using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class AdcProtocolViewModel : ObservableObject, IDisposable
{
    private const int MaximumLogEntries = 1000;
    private readonly object _frameLogGate;
    private readonly ObservableCollection<string> _frameLog;
    private readonly ListCollectionView _frameLogView;
    private string _pausedFrameLogText = "";

    private readonly IIoService _io;
    private readonly IAdcBus _pickupBus;
    private readonly IAdcBus _shootingBus;
    private readonly HantasSettings _settings;
    private readonly MachineController _machine;
    private readonly ILogger<AdcProtocolViewModel>? _log;
    private readonly ILogger<AdcBoltHead>? _headLog;
    private AdcBoltHead? _connectedHead;
    private (FasteningHead Head, byte Slave, string Port, int Baud)? _headConnection;
    private OperationCancellation.Operation? _operationCancellation;
    private readonly IAsyncRelayCommand[] _commands;
    private readonly MachineState _state;
    private bool _disposed;
    [ObservableProperty]
    public partial bool IsClosing { get; set; }
    [ObservableProperty]
    public partial string? CloseError { get; set; }
    [ObservableProperty]
    public partial string? SelectedPort { get; set; }
    [ObservableProperty]
    public partial FasteningHead SelectedHead { get; set; }
    [ObservableProperty]
    public partial int SelectedBaudRate { get; set; }
    [ObservableProperty]
    public partial string SlaveText { get; set; } = "0";
    [ObservableProperty]
    public partial AdcFunctionCode RegisterAccess { get; set; } = AdcFunctionCode.ReadInputRegisters;
    [ObservableProperty]
    public partial string AddressText { get; set; }
    [ObservableProperty]
    public partial string CountText { get; set; }
    [ObservableProperty]
    public partial string ValueText { get; set; } = "0";
    [ObservableProperty]
    public partial string SelectedLogText { get; set; } = "";

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; }
    [ObservableProperty]
    public partial string ResultMessage { get; set; }
    [ObservableProperty]
    public partial bool? ResultSuccess { get; private set; }
    [ObservableProperty]
    public partial string RegisterResult { get; set; } = "-";
    [ObservableProperty]
    public partial string[] PortNames { get; set; }
    [ObservableProperty]
    public partial bool IsLogPaused { get; set; }
    [ObservableProperty]
    public partial string? ClipboardError { get; set; }

    public AdcProtocolViewModel(
        IAdcBus pickupBus,
        IAdcBus shootingBus,
        IIoService io,
        HantasSettings settings,
        MachineController machine,
        MachineState state,
        ILogger<AdcProtocolViewModel>? log = null,
        ILogger<AdcBoltHead>? headLog = null)
    {
        _frameLogGate = new();
        _frameLog = new();
        AddressText = ((ushort)AdcResultRegister.EventCount).ToString();
        CountText = AdcFasteningResult.RegisterCount.ToString();
        PortNames = [];
        BaudRates = [9600, 19200, 38400, 57600, 115200];
        Heads = [FasteningHead.Pickup, FasteningHead.Shooting];
        RegisterAccesses = [
            AdcFunctionCode.ReadHoldingRegisters,
            AdcFunctionCode.ReadInputRegisters,
            AdcFunctionCode.WriteSingleRegister,
            AdcFunctionCode.RequestTorqueCurve,
        ];

        ToggleConnectionCommand = new AsyncRelayCommand(
            ToggleConnectionAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        SelectPresetCommand = new AsyncRelayCommand(
            SelectPresetAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        StartCommand = new AsyncRelayCommand(
            StartAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        StopCommand = new AsyncRelayCommand(
            StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions | AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        ReverseCommand = new AsyncRelayCommand(
            ReverseAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        ReleaseReverseCommand = new RelayCommand(ReleaseReverse);
        ResetAlarmCommand = new AsyncRelayCommand(
            ResetAlarmAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        ReadResultCommand = new AsyncRelayCommand(
            ReadResultAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        ReadDeviceInformationCommand = new AsyncRelayCommand(
            ReadDeviceInformationAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        CaptureDeviceInformationCommand = new AsyncRelayCommand(
            CaptureDeviceInformationAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        ExecuteRegisterCommand = new AsyncRelayCommand(
            ExecuteRegisterAsync, AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler);
        ClearLogCommand = new RelayCommand(ClearLog);
        CopyLogCommand = new RelayCommand(CopyLog);
        CopyAllLogCommand = new RelayCommand(CopyAllLog);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        _commands = [ToggleConnectionCommand, SelectPresetCommand, StartCommand, StopCommand,
            ReverseCommand, ResetAlarmCommand, ReadResultCommand, ReadDeviceInformationCommand,
            CaptureDeviceInformationCommand, ExecuteRegisterCommand];

        _io = io;
        _pickupBus = pickupBus;
        _shootingBus = shootingBus;
        _settings = settings;
        _machine = machine;
        _state = state;
        _log = log;
        _headLog = headLog;
        ConnectionStatus = UiText.Get("Disconnected");
        ResultMessage = UiText.Get("No result read");
        BindingOperations.EnableCollectionSynchronization(_frameLog, _frameLogGate);
        _frameLogView = new ListCollectionView(_frameLog);
        ((INotifyCollectionChanged)_frameLogView).CollectionChanged += OnFrameLogChanged;
        SelectedHead = FasteningHead.Pickup;
        _pickupBus.FrameTransferred += OnPickupFrameTransferred;
        _shootingBus.FrameTransferred += OnShootingFrameTransferred;
        _state.PropertyChanged += OnMachineStateChanged;
        foreach (var command in _commands)
            command.PropertyChanged += OnCommandChanged;

        RefreshControls();
    }

    public int[] BaudRates { get; }
    public FasteningHead[] Heads { get; }

    public IAdcBus Bus => SelectedHead == FasteningHead.Pickup ? _pickupBus : _shootingBus;
    public string ConnectionAction => Bus.IsOpen ? UiText.Get("Disconnect") : UiText.Get("Connect");

    public string FrameLogText
    {
        get
        {
            return IsLogPaused
                ? _pausedFrameLogText
                : string.Join(Environment.NewLine, _frameLogView.Cast<string>());
        }
    }

    public AdcFunctionCode[] RegisterAccesses { get; }

    public bool ConnectionControlsEnabled => !_disposed && !IsClosing && _operationCancellation is null;

    public bool PortSelectionEnabled => ConnectionControlsEnabled && _machine.IsUseAdcProtocolAllowed && !Bus.IsOpen;

    public bool SlaveSelectionEnabled => ConnectionControlsEnabled && !Bus.IsOpen && _machine.IsUseAdcProtocolAllowed;

    public bool ProtocolEnabled => ConnectionControlsEnabled && _machine.IsUseAdcProtocolAllowed && Bus.IsOpen;

    private byte SlaveAddress => byte.Parse(SlaveText);

    public async Task<bool> TryCloseAsync()
    {
        IsClosing = true;
        CloseError = null;
        RefreshControls();
        try
        {
            await ShutdownAsync();
            return true;
        }
        catch (Exception exception)
        {
            IsClosing = false;
            RefreshControls();
            CloseError = exception.Message;
            _log?.LogError(exception, "ADC diagnostic shutdown failed.");
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _operationCancellation?.Cancel();
        _pickupBus.FrameTransferred -= OnPickupFrameTransferred;
        _shootingBus.FrameTransferred -= OnShootingFrameTransferred;
        _state.PropertyChanged -= OnMachineStateChanged;
        foreach (var command in _commands)
            command.PropertyChanged -= OnCommandChanged;
        ((INotifyCollectionChanged)_frameLogView).CollectionChanged -= OnFrameLogChanged;
        _frameLogView.DetachFromSourceCollection();
    }

    partial void OnSelectedPortChanged(string? value)
    {
        OnPropertyChanged(nameof(IsConnectAllowed));
    }

    partial void OnSelectedHeadChanging(FasteningHead value)
    {
        if (!ConnectionControlsEnabled)
            throw new InvalidOperationException(UiText.Get("Wait for the ADC operation to stop before changing the selected head."));
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    partial void OnSelectedHeadChanged(FasteningHead value)
    {
        var pickup = value == FasteningHead.Pickup;
        SelectedPort = pickup ? _settings.PickupPortName : _settings.ShootingPortName;
        SelectedBaudRate = pickup ? _settings.PickupBaudRate : _settings.ShootingBaudRate;
        SlaveText = (pickup ? _settings.PickupSlaveAddress : _settings.ShootingSlaveAddress).ToString();
        ResultMessage = UiText.Get("No result read");
        RegisterResult = "-";
        RefreshPorts();
        if (Bus.IsOpen)
        {
            SelectedPort = Bus.PortName;
            SelectedBaudRate = Bus.BaudRate;
            ConnectionStatus = $"{Bus.PortName} | {Bus.BaudRate}";
        }
        OnPropertyChanged(nameof(Bus));
        RefreshControls();
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed)
            RefreshControls();
    }

    partial void OnResultMessageChanging(string value)
    {
        ResultSuccess = null;
    }

    public IAsyncRelayCommand ToggleConnectionCommand { get; }

    private async Task ToggleConnectionAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            if (Bus.IsOpen)
            {
                var connectedPort = Bus.PortName;
                await Task.Run(Bus.Close);
                _connectedHead = null;
                ConnectionStatus = UiText.Get("Disconnected");
                AppendLog($"DISCONNECT  {connectedPort}");
                return;
            }

            if (!byte.TryParse(SlaveText, out var slave))
            {
                ConnectionStatus = UiText.Get("Slave must be 0–255.");
                return;
            }

            var portName = SelectedPort!;
            var baudRate = SelectedBaudRate;
            await Task.Run(() => Bus.Open(portName, baudRate), operation.Token);
            _connectedHead = null;
            Bus.Monitor.IntervalMilliseconds = _settings.StatusPollMilliseconds;
            await Bus.Monitor.StartAsync(slave, operation.Token);
            ConnectionStatus = $"{portName} | {baudRate}";
            AppendLog($"CONNECT  {Bus.PortName} | {Bus.BaudRate}");
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand SelectPresetCommand { get; }

    private async Task SelectPresetAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            await ConnectedHead.SelectPresetAsync(1, operation.Token);
            ResultMessage = UiText.Get("Preset 1 selected");
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand StartCommand { get; }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            _machine.EnsureBoltTestAvailable();
            _state.BoltTestRunning = true;
            var head = ConnectedHead;
            var compensation = await head.ReadTorqueCompensationAsync(1, operation.Token);
            await head.SelectPresetAsync(1, operation.Token);
            ResultMessage = UiText.Get("Fastening...");
            var result = await head.TightenAsync(operation.Token,
                resultReceived: ShowResult, torqueCompensationPercent: compensation);
            ShowResult(result);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (operation is not null && _state.BoltTestRunning
                && exception is not OperationCanceledException)
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.BoltFastening, exception);
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
            {
                _state.BoltTestRunning = false;
                EndCommand(operation, failure);
            }
        }
    }

    public IAsyncRelayCommand StopCommand { get; }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            if (_operationCancellation is not null)
            {
                // Tighten/reverse turn START off in their cleanup; read commands do not.
                var stopsHead = StartCommand.IsRunning || ReverseCommand.IsRunning;
                var commands = _commands.Where(command => command != StopCommand).ToArray();
                var pending = CommandShutdown.Capture(commands);
                Task? stopping = null;
                if (!stopsHead)
                {
                    try
                    {
                        // The query still owns control. Its failure must not skip START OFF.
                        ResultMessage = UiText.Get("Turning START OFF...");
                        ConnectedHead.Stop();
                    }
                    catch (Exception exception)
                    {
                        stopping = Task.FromException(exception);
                    }
                }
                await CommandShutdown.CancelAndWaitAsync(commands, stopping, pending);
                if (!stopsHead)
                    ResultMessage = UiText.Get("START OFF sent; see controller feedback.");
                return;
            }
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            operation.Token.ThrowIfCancellationRequested();
            ResultMessage = UiText.Get("Turning START OFF...");
            ConnectedHead.Stop();
            ResultMessage = UiText.Get("START OFF sent; see controller feedback.");
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    private AdcBoltHead ConnectedHead
    {
        get
        {
            var connection = (SelectedHead, SlaveAddress, Bus.PortName, Bus.BaudRate);
            if (_connectedHead is null || _headConnection != connection)
            {
                _connectedHead = new(Bus, _io, SelectedHead, _settings,
                    SlaveAddress, Bus.PortName, Bus.BaudRate, _headLog);
                _headConnection = connection;
            }
            return _connectedHead;
        }
    }

    public IAsyncRelayCommand ReverseCommand { get; }

    private async Task ReverseAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            _machine.EnsureBoltTestAvailable();
            _state.BoltTestRunning = true;
            ResultMessage = UiText.Get("Loosening · release to stop");
            await ConnectedHead.RunReverseAsync(operation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (operation is not null && _state.BoltTestRunning
                && exception is not OperationCanceledException)
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.BoltFastening, exception);
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
            {
                _state.BoltTestRunning = false;
                EndCommand(operation, failure);
            }
        }
    }

    public IRelayCommand ReleaseReverseCommand { get; }

    private void ReleaseReverse()
    {
        ReverseCommand.Cancel();
    }

    public bool IsTestBoltHeadAllowed => ProtocolEnabled && _state.ManualSetupEnabled;

    public IAsyncRelayCommand ResetAlarmCommand { get; }

    private async Task ResetAlarmAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            await ConnectedHead.ResetAsync(operation.Token);
            ResultMessage = UiText.Get("I/O reset confirmed");
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand ReadResultCommand { get; }

    private async Task ReadResultAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            var bus = Bus;
            var slave = SlaveAddress;
            var result = await bus.Monitor.EnqueueAsync(
                token => bus.ReadFasteningResultAsync(slave, token), operation.Token);
            ResultMessage = UiText.Format($"Last result: {UiText.Get(result.Status)}  Event {result.EventCount}\n")
                + $"{UiText.Get("Preset")} {result.Preset}  {UiText.Get("Raw torque")} {result.Torque:F2} / {result.TargetTorque:F2}\n"
                + UiText.Format($"Time {result.FasteningTimeMilliseconds} ms\n")
                + UiText.Format($"Result error: {AdcControllerError.Describe(result.Error)}");
            ResultSuccess = result.Error != 0 ? false : result.Status switch
            {
                AdcEventStatus.FasteningOk => true,
                AdcEventStatus.FasteningNg or AdcEventStatus.Error => false,
                _ => null,
            };
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand ReadDeviceInformationCommand { get; }

    private async Task ReadDeviceInformationAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            var bus = Bus;
            var slave = SlaveAddress;
            var data = await bus.Monitor.EnqueueAsync(
                token => bus.ReadDeviceInformationAsync(slave, token), operation.Token);
            ResultMessage = UiText.Format($"Device data: {ToHex(data)}");
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand CaptureDeviceInformationCommand { get; }

    private async Task CaptureDeviceInformationAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            const int durationMilliseconds = 3000;
            var slave = SlaveAddress;
            IsLogPaused = false;
            ResultMessage = UiText.Get("Capturing raw RX · 3 s");
            AppendLog($"CAPTURE BEGIN  ADC {slave}, {Bus.PortName} | {Bus.BaudRate}, {durationMilliseconds} ms");
            var bus = Bus;
            var received = await bus.Monitor.EnqueueAsync(
                token => bus.CaptureDeviceInformationAsync(slave, durationMilliseconds, token), operation.Token);
            var request = AdcRtuFrame.Build(slave, AdcFunctionCode.RequestDeviceInformation, []);
            var summary = received.Length == 0
                ? UiText.Get("No bytes received.")
                : received.AsSpan().StartsWith(request)
                    ? UiText.Format($"RX starts with the TX frame; {received.Length - request.Length} byte(s) follow it.")
                    : UiText.Get("RX does not start with the TX frame.");
            ResultMessage = UiText.Format($"Captured {received.Length} bytes over {durationMilliseconds} ms. {summary}");
            AppendLog($"RX ALL (arrival order)  {ToHex(received)}");
            AppendLog($"CAPTURE END  ADC {slave}: {ResultMessage}");
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand ExecuteRegisterCommand { get; }

    private async Task ExecuteRegisterAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = _machine.BeginAdcProtocol(cancellationToken);
            _operationCancellation = operation;
            RefreshControls();
            RegisterResult = "-";
            var access = RegisterAccess;
            if (!ushort.TryParse(AddressText, out var address))
            {
                RegisterResult = UiText.Get("Address must be 0–65535.");
                return;
            }

            var bus = Bus;
            var slave = SlaveAddress;
            switch (access)
            {
                case AdcFunctionCode.RequestTorqueCurve:
                    var graph = await bus.Monitor.EnqueueAsync(
                        token => bus.RequestTorqueCurveAsync(slave, token), operation.Token);
                    RegisterResult = Convert.ToHexString(graph);
                    break;
                case AdcFunctionCode.ReadHoldingRegisters or AdcFunctionCode.ReadInputRegisters:
                    if (!ushort.TryParse(CountText, out var count))
                    {
                        RegisterResult = UiText.Get("Check register count.");
                        return;
                    }

                    var values = await bus.Monitor.EnqueueAsync(
                        token => bus.ReadRegistersAsync(slave, access, address, count, token), operation.Token);
                    RegisterResult = string.Join(
                        Environment.NewLine,
                        values.Select((value, index) => $"{address + index} = {value} (0x{value:X4})"));
                    break;
                case AdcFunctionCode.WriteSingleRegister:
                    if (!ushort.TryParse(ValueText, out var value))
                    {
                        RegisterResult = UiText.Get("Value must be 0–65535.");
                        return;
                    }

                    if (address == (ushort)AdcRemoteRegister.RemoteStart && value != 0)
                    {
                        RegisterResult = UiText.Get("Raw Start is not available. Use Start Fastening or Reverse (Hold).");
                        return;
                    }

                    await bus.Monitor.EnqueueAsync(async token =>
                    {
                        await bus.WriteRegisterAsync(slave, address, value, token);
                        return true;
                    }, operation.Token);
                    RegisterResult = $"{address} = {value} (0x{value:X4})";
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
            throw;
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IRelayCommand ClearLogCommand { get; }

    private void ClearLog()
    {
        lock (_frameLogGate)
            _frameLog.Clear();
        _pausedFrameLogText = "";
        ClipboardError = null;
        OnPropertyChanged(nameof(FrameLogText));
    }

    private void OnFrameLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsLogPaused)
            OnPropertyChanged(nameof(FrameLogText));
    }

    partial void OnIsLogPausedChanged(bool value)
    {
        _pausedFrameLogText = value
            ? string.Join(Environment.NewLine, _frameLogView.Cast<string>())
            : "";
        OnPropertyChanged(nameof(FrameLogText));
    }

    public IRelayCommand CopyLogCommand { get; }

    private void CopyLog()
    {
        CopyLogText(SelectedLogText.Length > 0 ? SelectedLogText : FrameLogText);
    }

    public IRelayCommand CopyAllLogCommand { get; }

    private void CopyAllLog()
    {
        CopyLogText(FrameLogText);
    }

    private void CopyLogText(string text)
    {
        try
        {
            if (text.Length > 0)
                Clipboard.SetText(text);
            ClipboardError = null;
        }
        catch (ExternalException exception)
        {
            ClipboardError = UiText.Format($"Clipboard is unavailable: {exception.Message}");
        }
    }

    private void EndCommand(OperationCancellation.Operation operation, Exception? failure)
    {
        try
        {
            operation.Dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
            ShowFailure(failure);
            throw failure;
        }
        finally
        {
            _operationCancellation = null;
            RefreshControls();
        }
    }

    private void ShowFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            ResultSuccess = null;
            ResultMessage = UiText.Get("Operation canceled. Check controller status.");
            return;
        }

        ResultSuccess = false;
        var message = UiText.Get("\nOperation failed. Check controller status.");
        if (!ResultMessage.EndsWith(message, StringComparison.Ordinal))
            ResultMessage += message;
        ConnectionStatus = exception.Message;
        _log?.LogError(exception, "ADC diagnostic operation failed.");
        AppendLog($"ERROR  {exception.Message}", record: false);
    }

    public bool IsConnectAllowed
    {
        get
        {
            return ConnectionControlsEnabled && _machine.IsUseAdcProtocolAllowed
                && (Bus.IsOpen || SelectedPort is not null);
        }
    }

    public bool IsStopAllowed
    {
        get
        {
            return !_disposed && !IsClosing
                && (_operationCancellation is not null || Bus.IsOpen && _machine.IsUseAdcProtocolAllowed);
        }
    }

    public void RefreshControls()
    {
        OnPropertyChanged(nameof(ConnectionAction));
        OnPropertyChanged(nameof(ConnectionControlsEnabled));
        OnPropertyChanged(nameof(PortSelectionEnabled));
        OnPropertyChanged(nameof(SlaveSelectionEnabled));
        OnPropertyChanged(nameof(ProtocolEnabled));
        OnPropertyChanged(nameof(IsTestBoltHeadAllowed));
        OnPropertyChanged(nameof(IsStopAllowed));
        OnPropertyChanged(nameof(IsConnectAllowed));
    }

    internal Task ShutdownAsync()
    {
        return CommandShutdown.CancelAndWaitAsync(_commands);
    }

    private void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning))
            return;
        // Commands report errors above and keep them on the actual task for STOP/close.
        if (sender is IAsyncRelayCommand { ExecutionTask.IsFaulted: true } command)
            _ = command.ExecutionTask.Exception;
        RefreshControls();
    }

    private void ShowResult(BoltResult result)
    {
        ResultMessage = $"{(result.Success ? "OK" : "NG")}  {UiText.Get("Result torque")} {result.CompensatedTorque?.ToString("F2") ?? "—"}";
        ResultMessage += $"\n{UiText.Get("Raw torque")}: {result.Torque?.ToString("F2") ?? "—"}";
        ResultMessage += $"\n{UiText.Get("Compensation (%)")}: {result.Controller?.TorqueCompensationPercent?.ToString() ?? "—"}";
        if (result.Error is not null)
            ResultMessage += $"\n{result.Error}";
        ResultSuccess = result.Success;
    }

    public IRelayCommand RefreshPortsCommand { get; }

    private void RefreshPorts()
    {
        var selected = SelectedPort ?? (SelectedHead == FasteningHead.Pickup
            ? _settings.PickupPortName : _settings.ShootingPortName);
        var ports = Bus.PortNames;
        PortNames = ports;
        SelectedPort = ports.FirstOrDefault(
            port => string.Equals(port, selected, StringComparison.OrdinalIgnoreCase));
        if (!Bus.IsOpen)
        {
            ConnectionStatus = ports.Length == 0
                ? UiText.Get("No serial ports found")
                : SelectedPort is null ? UiText.Get("Select a serial port") : UiText.Get("Disconnected");
        }

        RefreshControls();
    }

    private void OnPickupFrameTransferred(AdcFrameDirection direction, byte[] frame)
    {
        AppendLog(
            $"Pickup [{_pickupBus.PortName}] {(direction == AdcFrameDirection.Transmit ? "TX" : "RX RAW")}     {ToHex(frame)}",
            record: false);
    }

    private void OnShootingFrameTransferred(AdcFrameDirection direction, byte[] frame)
    {
        AppendLog(
            $"Shooting [{_shootingBus.PortName}] {(direction == AdcFrameDirection.Transmit ? "TX" : "RX RAW")}     {ToHex(frame)}",
            record: false);
    }

    private void AppendLog(string text, bool record = true)
    {
        if (record)
            _log?.LogInformation("ADC {Message}", text);
        lock (_frameLogGate)
        {
            _frameLog.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
            if (_frameLog.Count > MaximumLogEntries)
                _frameLog.RemoveAt(_frameLog.Count - 1);
        }
    }

    private static string ToHex(byte[] data)
    {
        return string.Join(' ', data.Select(value => value.ToString("X2")));
    }
}
