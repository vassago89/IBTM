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
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;
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
    private OperationCancellation.Operation? _operationCancellation;
    private TaskCompletionSource? _operationCompletion;
    private readonly MachineState _state;
    private readonly Dispatcher _dispatcher;
    private int _refreshQueued;
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
    public partial AdcEventStatus NextResult { get; set; } = AdcEventStatus.FasteningOk;
    [ObservableProperty]
    public partial string SelectedLogText { get; set; } = "";

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "Disconnected";
    [ObservableProperty]
    public partial string ConnectionAction { get; set; } = "Connect";
    [ObservableProperty]
    public partial string ResultMessage { get; set; } = "No result read";
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
        ILogger<AdcProtocolViewModel>? log = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
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
        ];
        FasteningResults = [AdcEventStatus.FasteningOk, AdcEventStatus.FasteningNg, AdcEventStatus.Error,];

        QueueResultCommand = new RelayCommand(QueueResult);
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => IsConnectAllowed);
        SelectPresetCommand = new AsyncRelayCommand(SelectPresetAsync, () => ProtocolEnabled);
        StartCommand = new AsyncRelayCommand(StartAsync, () => IsTestBoltHeadAllowed);
        StopCommand = new AsyncRelayCommand(
            StopAsync, () => IsStopAllowed, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ReverseCommand = new AsyncRelayCommand(ReverseAsync, () => IsTestBoltHeadAllowed);
        ReleaseReverseCommand = new RelayCommand(ReleaseReverse);
        ResetAlarmCommand = new AsyncRelayCommand(ResetAlarmAsync, () => ProtocolEnabled);
        ReadResultCommand = new AsyncRelayCommand(ReadResultAsync, () => ProtocolEnabled);
        ReadDeviceInformationCommand = new AsyncRelayCommand(ReadDeviceInformationAsync, () => ProtocolEnabled);
        CaptureDeviceInformationCommand = new AsyncRelayCommand(CaptureDeviceInformationAsync, () => ProtocolEnabled);
        ExecuteRegisterCommand = new AsyncRelayCommand(ExecuteRegisterAsync, () => ProtocolEnabled);
        ClearLogCommand = new RelayCommand(ClearLog);
        CopyLogCommand = new RelayCommand(CopyLog);
        CopyAllLogCommand = new RelayCommand(CopyAllLog);
        RefreshPortsCommand = new RelayCommand(RefreshPorts, () => PortSelectionEnabled);

        _io = io;
        _pickupBus = pickupBus;
        _shootingBus = shootingBus;
        _settings = settings;
        _machine = machine;
        _state = state;
        _log = log;
        BindingOperations.EnableCollectionSynchronization(_frameLog, _frameLogGate);
        _frameLogView = new ListCollectionView(_frameLog);
        ((INotifyCollectionChanged)_frameLogView).CollectionChanged += OnFrameLogChanged;
        SelectedHead = FasteningHead.Pickup;
        _pickupBus.FrameTransferred += OnPickupFrameTransferred;
        _shootingBus.FrameTransferred += OnShootingFrameTransferred;
        _state.PropertyChanged += OnMachineStateChanged;

        RefreshControls();
    }

    public int[] BaudRates { get; }
    public FasteningHead[] Heads { get; }

    private IAdcBus Bus => SelectedHead == FasteningHead.Pickup ? _pickupBus : _shootingBus;
    public AdcStatusMonitor Monitor => Bus.Monitor;

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

    public bool IsVirtual => Bus is VirtualAdcBus;

    public AdcEventStatus[] FasteningResults { get; }

    public bool ConnectionControlsEnabled => !_disposed && !IsClosing && _operationCancellation is null;

    public bool PortSelectionEnabled => ConnectionControlsEnabled && _machine.IsUseAdcProtocolAllowed && !Bus.IsOpen;

    public bool SlaveSelectionEnabled => ConnectionControlsEnabled && !Bus.IsOpen
        && (IsVirtual || _machine.IsUseAdcProtocolAllowed);

    public bool ProtocolEnabled => ConnectionControlsEnabled && _machine.IsUseAdcProtocolAllowed && Bus.IsOpen;

    public bool VirtualResultEnabled => !IsClosing;

    private byte SlaveAddress => byte.Parse(SlaveText);

    public IRelayCommand QueueResultCommand { get; }

    private void QueueResult()
    {
        switch (Bus)
        {
            case VirtualAdcBus virtualBus when byte.TryParse(SlaveText, out var slave):
                var result = NextResult;
                virtualBus.SetNextFasteningResult(slave, result);
                AppendLog($"VIRTUAL  Slave {slave}: {result.GetDescription()} set for one fastening");
                break;
            case VirtualAdcBus:
                ConnectionStatus = "Enter a valid slave address";
                break;
        }
    }

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
        ((INotifyCollectionChanged)_frameLogView).CollectionChanged -= OnFrameLogChanged;
        _frameLogView.DetachFromSourceCollection();
    }

    partial void OnSelectedPortChanged(string? value)
    {
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedHeadChanging(FasteningHead value)
    {
        if (!ConnectionControlsEnabled)
            throw new InvalidOperationException("Wait for the ADC operation to stop before changing the selected head.");
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    partial void OnSelectedHeadChanged(FasteningHead value)
    {
        var pickup = value == FasteningHead.Pickup;
        SelectedPort = pickup ? _settings.PickupPortName : _settings.ShootingPortName;
        SelectedBaudRate = pickup ? _settings.PickupBaudRate : _settings.ShootingBaudRate;
        SlaveText = (pickup ? _settings.PickupSlaveAddress : _settings.ShootingSlaveAddress).ToString();
        ResultMessage = "No result read";
        RegisterResult = "-";
        RefreshPorts();
        ConnectionAction = Bus.IsOpen ? "Disconnect" : "Connect";
        if (Bus.IsOpen)
        {
            SelectedPort = Bus.PortName;
            SelectedBaudRate = Bus.BaudRate;
            ConnectionStatus = $"{Bus.PortName} | {Bus.BaudRate}";
        }
        OnPropertyChanged(nameof(IsVirtual));
        OnPropertyChanged(nameof(Monitor));
        RefreshControls();
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed)
                RefreshControls();
        });
    }

    public IAsyncRelayCommand ToggleConnectionCommand { get; }

    private async Task ToggleConnectionAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            if (Bus.IsOpen)
            {
                var connectedPort = Bus.PortName;
                await Task.Run(Bus.Close);
                ConnectionAction = "Connect";
                ConnectionStatus = "Disconnected";
                AppendLog($"DISCONNECT  {connectedPort}");
                return;
            }

            var portName = SelectedPort!;
            var baudRate = SelectedBaudRate;
            await Task.Run(() => Bus.Open(portName, baudRate), operation.Token);
            Monitor.IntervalMilliseconds = _settings.StatusPollMilliseconds;
            await Monitor.StartAsync(SlaveAddress, operation.Token);
            ConnectionAction = "Disconnect";
            ConnectionStatus = $"{portName} | {baudRate}";
            AppendLog($"CONNECT  {Bus.PortName} | {Bus.BaudRate}");
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand SelectPresetCommand { get; }

    private async Task SelectPresetAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            await CreateHead().SelectPresetAsync(1, operation.Token);
            ResultMessage = "Preset 1 selected";
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand StartCommand { get; }

    private async Task StartAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            _machine.EnsureBoltTestAvailable();
            try
            {
                _state.BoltTestRunning = true;
                var head = CreateHead();
                await head.SelectPresetAsync(1, operation.Token);
                ResultMessage = "Fastening...";
                var result = await head.TightenAsync(operation.Token);
                ResultMessage = $"{(result.Success ? "OK" : "NG")}  Torque {result.Torque:F2}";
                if (result.Error is not null)
                    ResultMessage += $"\n{result.Error}";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.BoltFastening, exception);
                throw;
            }
            finally
            {
                _state.BoltTestRunning = false;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand StopCommand { get; }

    private async Task StopAsync()
    {
        if (_operationCancellation is { } cancellation)
        {
            var completion = _operationCompletion!.Task;
            cancellation.Cancel();
            try
            {
                await completion;
            }
            catch (Exception exception)
            {
                ShowFailure(exception);
            }
            return;
        }

        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            operation.Token.ThrowIfCancellationRequested();
            ResultMessage = "Turning START OFF...";
            await CreateHead().StopAsync();
            ResultMessage = "Stopped";
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    private AdcBoltHead CreateHead()
    {
        return new(
            Bus,
            _io, SelectedHead,
            _settings,
            SlaveAddress,
            Bus.PortName,
            Bus.BaudRate);
    }

    public IAsyncRelayCommand ReverseCommand { get; }

    private async Task ReverseAsync(CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(cancellationToken);
            _machine.EnsureBoltTestAvailable();
            try
            {
                _state.BoltTestRunning = true;
                ResultMessage = "Loosening — hold to run; release to stop. No automatic completion judgement.";
                await CreateHead().RunReverseAsync(operation.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.BoltFastening, exception);
                throw;
            }
            finally
            {
                _state.BoltTestRunning = false;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IRelayCommand ReleaseReverseCommand { get; }

    private void ReleaseReverse()
    {
        ReverseCommand.Cancel();
    }

    private bool IsTestBoltHeadAllowed => ProtocolEnabled && _machine.IsTestBoltHeadAllowed;

    public IAsyncRelayCommand ResetAlarmCommand { get; }

    private async Task ResetAlarmAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            await CreateHead().ResetAsync(operation.Token);
            ResultMessage = "I/O reset confirmed";
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand ReadResultCommand { get; }

    private async Task ReadResultAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            var bus = Bus;
            var slave = SlaveAddress;
            var result = await bus.Monitor.EnqueueAsync(
                token => bus.ReadFasteningResultAsync(slave, token), operation.Token);
            ResultMessage = $"Last result: {result.Status.GetDescription()}  Event {result.EventCount}\n"
                + $"Preset {result.Preset}  Torque {result.Torque:F2} / {result.TargetTorque:F2}\n"
                + $"Time {result.FasteningTimeMilliseconds} ms\n"
                + $"Result error: {AdcControllerError.Describe(result.Error)}";
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand ReadDeviceInformationCommand { get; }

    private async Task ReadDeviceInformationAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            var bus = Bus;
            var slave = SlaveAddress;
            var data = await bus.Monitor.EnqueueAsync(
                token => bus.ReadDeviceInformationAsync(slave, token), operation.Token);
            ResultMessage = $"Device data: {ToHex(data)}";
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand CaptureDeviceInformationCommand { get; }

    private async Task CaptureDeviceInformationAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            const int durationMilliseconds = 3000;
            var slave = SlaveAddress;
            IsLogPaused = false;
            ResultMessage = "Capturing raw RX for 3 seconds; no response parsing.";
            AppendLog($"CAPTURE BEGIN  ADC {slave}, {Bus.PortName} | {Bus.BaudRate}, {durationMilliseconds} ms");
            var bus = Bus;
            var received = await bus.Monitor.EnqueueAsync(
                token => bus.CaptureDeviceInformationAsync(slave, durationMilliseconds, token), operation.Token);
            var request = AdcRtuFrame.Build(slave, AdcFunctionCode.RequestDeviceInformation, []);
            var summary = received.Length == 0 ? "No bytes received." : received.AsSpan().StartsWith(request) ? $"RX starts with the TX frame; {received.Length - request.Length} byte(s) follow it." : "RX does not start with the TX frame.";
            ResultMessage = $"Captured {received.Length} bytes over {durationMilliseconds} ms. {summary}";
            AppendLog($"RX ALL (arrival order)  {ToHex(received)}");
            AppendLog($"CAPTURE END  ADC {slave}: {ResultMessage}");
        }
        catch (Exception exception)
        {
            failure = exception;
            ShowFailure(exception);
        }
        finally
        {
            if (operation is not null)
                EndCommand(operation, failure);
        }
    }

    public IAsyncRelayCommand ExecuteRegisterCommand { get; }

    private async Task ExecuteRegisterAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            var access = RegisterAccess;
            var address = ushort.Parse(AddressText);
            var bus = Bus;
            var slave = SlaveAddress;
            switch (access)
            {
                case AdcFunctionCode.ReadHoldingRegisters or AdcFunctionCode.ReadInputRegisters:
                    var count = ushort.Parse(CountText);
                    var values = await bus.Monitor.EnqueueAsync(
                        token => bus.ReadRegistersAsync(slave, access, address, count, token), operation.Token);
                    RegisterResult = string.Join(
                        Environment.NewLine,
                        values.Select((value, index) => $"{address + index} = {value} (0x{value:X4})"));
                    break;
                case AdcFunctionCode.WriteSingleRegister:
                    var value = ushort.Parse(ValueText);
                    if (address == (ushort)AdcRemoteRegister.RemoteStart && value != 0)
                    {
                        RegisterResult = "Raw Start is not available. Use Start Fastening or Reverse (Hold).";
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
            ClipboardError = $"Clipboard is unavailable: {exception.Message}";
        }
    }

    private OperationCancellation.Operation BeginCommand(CancellationToken cancellationToken)
    {
        var operation = _machine.BeginAdcProtocol(cancellationToken);
        _operationCancellation = operation;
        _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _state.Changed += StopWhenUnavailable;
        try
        {
            StopWhenUnavailable();
            operation.Token.ThrowIfCancellationRequested();
            RefreshControls();
            return operation;
        }
        catch (Exception exception)
        {
            EndCommand(operation, exception);
            throw;
        }
    }

    private void StopWhenUnavailable()
    {
        if (!_machine.AdcProtocolAvailable)
            _operationCancellation?.Cancel();
    }

    private void EndCommand(OperationCancellation.Operation operation, Exception? failure)
    {
        _state.Changed -= StopWhenUnavailable;
        try
        {
            operation.Dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
            ShowFailure(failure);
        }
        finally
        {
            _operationCancellation = null;
            if (failure is OperationCanceledException)
                _operationCompletion!.TrySetCanceled();
            else if (failure is not null)
            {
                _operationCompletion!.TrySetException(failure);
                _ = _operationCompletion.Task.Exception;
            }
            else
                _operationCompletion!.TrySetResult();
            RefreshControls();
        }
    }

    private void ShowFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            ResultMessage = "Operation canceled. Check controller status.";
            return;
        }

        ResultMessage = "Operation failed. Check controller status.";
        ConnectionStatus = exception.Message;
        _log?.LogError(exception, "ADC diagnostic operation failed.");
        AppendLog($"ERROR  {exception.Message}", record: false);
    }

    private bool IsConnectAllowed
    {
        get
        {
            return ConnectionControlsEnabled && _machine.IsUseAdcProtocolAllowed
                && (Bus.IsOpen || SelectedPort is not null);
        }
    }

    private bool IsStopAllowed
    {
        get
        {
            return !_disposed && !IsClosing
                && (_operationCancellation is not null || Bus.IsOpen && _machine.IsUseAdcProtocolAllowed);
        }
    }

    public void RefreshControls()
    {
        OnPropertyChanged(nameof(ConnectionControlsEnabled));
        OnPropertyChanged(nameof(PortSelectionEnabled));
        OnPropertyChanged(nameof(SlaveSelectionEnabled));
        OnPropertyChanged(nameof(ProtocolEnabled));
        OnPropertyChanged(nameof(VirtualResultEnabled));
        RefreshPortsCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        SelectPresetCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        ReverseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetAlarmCommand.NotifyCanExecuteChanged();
        ReadResultCommand.NotifyCanExecuteChanged();
        ReadDeviceInformationCommand.NotifyCanExecuteChanged();
        CaptureDeviceInformationCommand.NotifyCanExecuteChanged();
        ExecuteRegisterCommand.NotifyCanExecuteChanged();
    }

    internal async Task ShutdownAsync()
    {
        var operation = _operationCompletion?.Task ?? Task.CompletedTask;
        if (operation.IsCompleted)
        {
            return;
        }

        _operationCancellation?.Cancel();
        await CommandShutdown.WaitAsync(operation);
    }

    public IRelayCommand RefreshPortsCommand { get; }

    private void RefreshPorts()
    {
        var selected = SelectedPort ?? (SelectedHead == FasteningHead.Pickup
            ? _settings.PickupPortName : _settings.ShootingPortName);
        var ports = Bus.GetPortNames();
        PortNames = ports;
        SelectedPort = ports.FirstOrDefault(
            port => string.Equals(port, selected, StringComparison.OrdinalIgnoreCase));
        if (!Bus.IsOpen)
        {
            ConnectionStatus = ports.Length == 0
                ? "No serial ports found"
                : SelectedPort is null ? "Select a serial port" : "Disconnected";
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
            _log?.LogInformation("{Message}", $"ADC {text}");
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
