using System;
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

namespace IBTM.UI;

public partial class AdcProtocolViewModel : ObservableObject, IDisposable
{
    private const int MaximumLogEntries = 1000;
    private readonly object _frameLogGate = new();
    private readonly ObservableCollection<string> _frameLog = new();
    private readonly ListCollectionView _frameLogView;
    private string _pausedFrameLogText = "";

    private readonly IAdcBus _bus;
    private readonly HantasSettings _settings;
    private readonly MachineController _machine;
    private readonly ApplicationLog? _log;
    private OperationCancellation.Operation? _operationCancellation;
    private TaskCompletionSource? _operationCompletion;
    private readonly MachineState _state;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private int _refreshQueued;
    private bool _disposed;
    [ObservableProperty]
    private bool _isClosing;
    [ObservableProperty]
    private string? _closeError;
    [ObservableProperty]
    private string? _selectedPort;
    [ObservableProperty]
    private int _selectedBaudRate;
    [ObservableProperty]
    private string _slaveText = "0";
    [ObservableProperty]
    private string _presetText = "1";
    [ObservableProperty]
    private AdcFunctionCode _registerAccess = AdcFunctionCode.ReadInputRegisters;
    [ObservableProperty]
    private string _addressText = ((ushort)AdcResultRegister.EventCount).ToString();
    [ObservableProperty]
    private string _countText = AdcFasteningResult.RegisterCount.ToString();
    [ObservableProperty]
    private string _valueText = "0";
    [ObservableProperty]
    private AdcEventStatus _nextResult = AdcEventStatus.FasteningOk;
    [ObservableProperty]
    private string _selectedLogText = "";

    [ObservableProperty]
    private string _connectionStatus = "Disconnected";
    [ObservableProperty]
    private string _connectionAction = "Connect";
    [ObservableProperty]
    private string _resultMessage = "No result read";
    [ObservableProperty]
    private string _registerResult = "-";
    [ObservableProperty]
    private string[] _portNames = [];
    [ObservableProperty]
    private bool _isLogPaused;
    [ObservableProperty]
    private string? _clipboardError;

    public AdcProtocolViewModel(
        IAdcBus bus,
        HantasSettings settings,
        MachineController machine,
        MachineState state,
        ApplicationLog? log = null)
    {
        QueueResultCommand = new RelayCommand(QueueResult);
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync, CanConnect);
        SelectPresetCommand = new AsyncRelayCommand(SelectPresetAsync, () => ProtocolEnabled);
        StartCommand = new AsyncRelayCommand(StartAsync, CanTestBoltHead);
        StopCommand = new AsyncRelayCommand(
            StopAsync, CanStop, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ReverseCommand = new AsyncRelayCommand(ReverseAsync, CanTestBoltHead);
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

        _bus = bus;
        _settings = settings;
        _machine = machine;
        _state = state;
        _log = log;
        BindingOperations.EnableCollectionSynchronization(_frameLog, _frameLogGate);
        _frameLogView = new ListCollectionView(_frameLog);
        ((INotifyCollectionChanged)_frameLogView).CollectionChanged += OnFrameLogChanged;
        RefreshPorts();
        SelectedBaudRate = settings.BaudRate;
        SlaveText = settings.PickupSlaveAddress.ToString();
        _bus.FrameTransferred += OnFrameTransferred;
        _state.DisplayChanged += OnMachineStateChanged;
        if (_bus.IsOpen)
        {
            SelectedPort = _bus.PortName;
            SelectedBaudRate = _bus.BaudRate;
            ConnectionAction = "Disconnect";
            ConnectionStatus = $"{_bus.PortName} | {_bus.BaudRate}";
        }

        RefreshControls();
    }

    public int[] BaudRates { get; } = [9600, 19200, 38400, 57600, 115200];

    public string FrameLogText
    {
        get
        {
            return IsLogPaused
                ? _pausedFrameLogText
                : string.Join(Environment.NewLine, _frameLogView.Cast<string>());
        }
    }

    public AdcFunctionCode[] RegisterAccesses { get; } = [
        AdcFunctionCode.ReadHoldingRegisters,
        AdcFunctionCode.ReadInputRegisters,
        AdcFunctionCode.WriteSingleRegister,
    ];

    public bool IsVirtual
    {
        get
        {
            return _bus is VirtualAdcBus;
        }
    }

    public AdcEventStatus[] FasteningResults { get; } = [AdcEventStatus.FasteningOk, AdcEventStatus.FasteningNg, AdcEventStatus.Error,];

    public bool ConnectionControlsEnabled
    {
        get
        {
            return !_disposed && !IsClosing && _operationCancellation is null;
        }
    }

    public bool PortSelectionEnabled
    {
        get
        {
            return ConnectionControlsEnabled && _machine.CanUseAdcProtocol && !_bus.IsOpen;
        }
    }

    public bool SlaveSelectionEnabled
    {
        get
        {
            return ConnectionControlsEnabled && (IsVirtual || _machine.CanUseAdcProtocol);
        }
    }

    public bool ProtocolEnabled
    {
        get
        {
            return ConnectionControlsEnabled && _machine.CanUseAdcProtocol && _bus.IsOpen;
        }
    }

    public bool VirtualResultEnabled
    {
        get
        {
            return !IsClosing;
        }
    }

    private byte SlaveAddress
    {
        get
        {
            return byte.Parse(SlaveText);
        }
    }

    public IRelayCommand QueueResultCommand { get; }

    private void QueueResult()
    {
        if (_bus is not VirtualAdcBus virtualBus)
        {
            return;
        }

        if (!byte.TryParse(SlaveText, out var slave))
        {
            ConnectionStatus = "Enter a valid slave address";
            return;
        }

        var result = NextResult;
        virtualBus.SetNextFasteningResult(slave, result);
        AppendLog($"VIRTUAL  Slave {slave}: {result.GetDescription()} set for one fastening");
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
            _log?.Error("ADC diagnostic shutdown failed.", exception);
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _operationCancellation?.Cancel();
        _bus.FrameTransferred -= OnFrameTransferred;
        _state.DisplayChanged -= OnMachineStateChanged;
        ((INotifyCollectionChanged)_frameLogView).CollectionChanged -= OnFrameLogChanged;
        _frameLogView.DetachFromSourceCollection();
    }

    partial void OnSelectedPortChanged(string? value)
    {
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    private void OnMachineStateChanged()
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
            if (_bus.IsOpen)
            {
                var connectedPort = _bus.PortName;
                await Task.Run(_bus.Close);
                ConnectionAction = "Connect";
                ConnectionStatus = "Disconnected";
                AppendLog($"DISCONNECT  {connectedPort}");
                return;
            }

            var portName = SelectedPort!;
            var baudRate = SelectedBaudRate;
            await Task.Run(() => _bus.Open(portName, baudRate), operation.Token);
            ConnectionAction = "Disconnect";
            ConnectionStatus = $"{portName} | {baudRate}";
            AppendLog($"CONNECT  {_bus.PortName} | {_bus.BaudRate}");
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
            var preset = ushort.Parse(PresetText);
            await CreateHead().SelectPresetAsync(preset, operation.Token);
            ResultMessage = $"Preset {preset} selected";
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
                _state.SetBoltTestRunning(true);
                var head = CreateHead();
                await head.CheckReadyAsync(operation.Token);
                ResultMessage = "Fastening...";
                var result = await head.TightenAsync(operation.Token);
                ResultMessage = $"{(result.Success ? "OK" : "NG")}  Torque {result.Torque:F2}";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.BoltFastening, exception);
                throw;
            }
            finally
            {
                _state.SetBoltTestRunning(false);
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
            await ShowResultAsync(completion);
            return;
        }

        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            operation.Token.ThrowIfCancellationRequested();
            ResultMessage = "Stopping — waiting for RUN OFF...";
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
            _bus,
            new HantasSettings
            {
                PortName = _bus.PortName,
                BaudRate = _bus.BaudRate,
                ResponseTimeoutMilliseconds = _settings.ResponseTimeoutMilliseconds,
                FasteningTimeoutMilliseconds = _settings.FasteningTimeoutMilliseconds,
            },
            SlaveAddress);
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
                _state.SetBoltTestRunning(true);
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
                _state.SetBoltTestRunning(false);
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

    private bool CanTestBoltHead()
    {
        return ProtocolEnabled && _machine.CanTestBoltHead;
    }

    public IAsyncRelayCommand ResetAlarmCommand { get; }

    private async Task ResetAlarmAsync()
    {
        OperationCancellation.Operation? operation = null;
        Exception? failure = null;
        try
        {
            operation = BeginCommand(CancellationToken.None);
            await _bus.ResetAlarmAsync(SlaveAddress, operation.Token);
            ResultMessage = "Alarm reset sent";
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
            var result = await _bus.ReadFasteningResultAsync(SlaveAddress, operation.Token);
            var current = await _bus.ReadControllerStatusAsync(SlaveAddress, operation.Token);
            ResultMessage = $"Current: Ready {current.Ready}  Run {current.Running}  Alarm {current.Alarm}  Preset {current.Preset}\n" + $"Direction: {current.Direction.GetDescription()}\n" + $"Last result: {result.Status.GetDescription()}  Event {result.EventCount}\n" + $"Preset {result.Preset}  Torque {result.Torque:F2} / {result.TargetTorque:F2}\n" + $"Time {result.FasteningTimeMilliseconds} ms  Error {result.Error}";
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
            var data = await _bus.ReadDeviceInformationAsync(SlaveAddress, operation.Token);
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
            AppendLog($"CAPTURE BEGIN  ADC {slave}, {_bus.PortName} | {_bus.BaudRate}, {durationMilliseconds} ms");
            var received = await _bus.CaptureDeviceInformationAsync(slave, durationMilliseconds, operation.Token);
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
            switch (access)
            {
                case AdcFunctionCode.ReadHoldingRegisters:
                case AdcFunctionCode.ReadInputRegisters:
                    RegisterResult = FormatRegisters(address, await _bus.ReadRegistersAsync(SlaveAddress, access, address, ushort.Parse(CountText), operation.Token));
                    break;
                case AdcFunctionCode.WriteSingleRegister:
                    var value = ushort.Parse(ValueText);
                    if (address == (ushort)AdcRemoteRegister.RemoteStart && value != 0)
                    {
                        RegisterResult = "Raw Start is not available. Use Start Fastening or Reverse (Hold).";
                        return;
                    }

                    await _bus.WriteRegisterAsync(SlaveAddress, address, value, operation.Token);
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
        _log?.Error("ADC diagnostic operation failed.", exception);
        AppendLog($"ERROR  {exception.Message}", record: false);
    }

    private async Task ShowResultAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            ShowFailure(exception);
        }
    }

    private bool CanConnect()
    {
        return ConnectionControlsEnabled && _machine.CanUseAdcProtocol
            && (_bus.IsOpen || SelectedPort is not null);
    }

    private bool CanStop()
    {
        return !_disposed && !IsClosing
            && (_operationCancellation is not null || _bus.IsOpen && _machine.CanUseAdcProtocol);
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
        var selected = SelectedPort ?? _settings.PortName;
        var ports = _bus.GetPortNames();
        PortNames = ports;
        SelectedPort = ports.FirstOrDefault(
            port => string.Equals(port, selected, StringComparison.OrdinalIgnoreCase));
        if (!_bus.IsOpen)
        {
            ConnectionStatus = ports.Length == 0
                ? "No serial ports found"
                : SelectedPort is null ? "Select a serial port" : "Disconnected";
        }

        RefreshControls();
    }

    private void OnFrameTransferred(AdcFrameDirection direction, byte[] frame)
    {
        AppendLog(
            $"{(direction == AdcFrameDirection.Transmit ? "TX" : "RX RAW")}     {ToHex(frame)}",
            record: false);
    }

    private void AppendLog(string text, bool record = true)
    {
        if (record)
            _log?.Write($"ADC {text}");
        lock (_frameLogGate)
        {
            _frameLog.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
            if (_frameLog.Count > MaximumLogEntries)
                _frameLog.RemoveAt(_frameLog.Count - 1);
        }
    }

    private static string FormatRegisters(ushort address, ushort[] values)
    {
        return string.Join(
            Environment.NewLine,
            values.Select((value, index) => $"{address + index} = {value} (0x{value:X4})"));
    }

    private static string ToHex(byte[] data)
    {
        return string.Join(' ', data.Select(value => value.ToString("X2")));
    }
}
