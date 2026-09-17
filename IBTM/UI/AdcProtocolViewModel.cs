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
    private CancellationTokenSource? _operationCancellation;
    private Task _operation = Task.CompletedTask;
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

    [RelayCommand]
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

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ToggleConnectionAsync()
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
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
                await Task.Run(() => _bus.Open(portName, baudRate), cancellationToken);
                ConnectionAction = "Disconnect";
                ConnectionStatus = $"{portName} | {baudRate}";
                AppendLog($"CONNECT  {_bus.PortName} | {_bus.BaudRate}");
            });
    }

    [RelayCommand(CanExecute = nameof(ProtocolEnabled))]
    private async Task SelectPresetAsync()
    {
        await ExecuteAsync(async cancellationToken =>
        {
            var preset = ushort.Parse(PresetText);
            await CreateHead().SelectPresetAsync(preset, cancellationToken);
            ResultMessage = $"Preset {preset} selected";
        });
    }

    [RelayCommand(CanExecute = nameof(CanTestBoltHead))]
    private async Task StartAsync()
    {
        await ExecuteAsync(TestFasteningAsync);
    }

    [RelayCommand(CanExecute = nameof(CanStop), AllowConcurrentExecutions = true)]
    private async Task StopAsync()
    {
        if (_operationCancellation is { } cancellation)
        {
            cancellation.Cancel();
            await ShowResultAsync(_operation);
            return;
        }

        await ExecuteAsync(StopControllerAsync);
    }

    private async Task StopControllerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResultMessage = "Stopping — waiting for RUN OFF...";
        await CreateHead().StopAsync();
        ResultMessage = "Stopped";
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

    private Task TestFasteningAsync(CancellationToken cancellationToken)
    {
        return _machine.RunBoltTestAsync(
            async token =>
            {
                var head = CreateHead();
                await head.CheckReadyAsync(token);
                ResultMessage = "Fastening...";
                var result = await head.TightenAsync(token);
                ResultMessage = $"{(result.Success ? "OK" : "NG")}  Torque {result.Torque:F2}";
            },
            cancellationToken);
    }

    private Task TestReverseAsync(CancellationToken cancellationToken)
    {
        return _machine.RunBoltTestAsync(
            async token =>
            {
                ResultMessage = "Loosening — hold to run; release to stop. No automatic completion judgement.";
                await CreateHead().RunReverseAsync(token);
            },
            cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanTestBoltHead))]
    private Task ReverseAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync(TestReverseAsync, cancellationToken);
    }

    [RelayCommand]
    private void ReleaseReverse()
    {
        ReverseCommand.Cancel();
    }

    private bool CanTestBoltHead()
    {
        return ProtocolEnabled && _machine.CanTestBoltHead;
    }

    [RelayCommand(CanExecute = nameof(ProtocolEnabled))]
    private async Task ResetAlarmAsync()
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                await _bus.ResetAlarmAsync(SlaveAddress, cancellationToken);
                ResultMessage = "Alarm reset sent";
            });
    }

    [RelayCommand(CanExecute = nameof(ProtocolEnabled))]
    private async Task ReadResultAsync()
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                var result = await _bus.ReadFasteningResultAsync(SlaveAddress, cancellationToken);
                var current = await _bus.ReadControllerStatusAsync(SlaveAddress, cancellationToken);
                ResultMessage =
                    $"Current: Ready {current.Ready}  Run {current.Running}  Alarm {current.Alarm}  Preset {current.Preset}\n"
                    + $"Direction: {current.Direction.GetDescription()}\n"
                    + $"Last result: {result.Status.GetDescription()}  Event {result.EventCount}\n"
                    + $"Preset {result.Preset}  Torque {result.Torque:F2} / {result.TargetTorque:F2}\n"
                    + $"Time {result.FasteningTimeMilliseconds} ms  Error {result.Error}";
            });
    }

    [RelayCommand(CanExecute = nameof(ProtocolEnabled))]
    private async Task ReadDeviceInformationAsync()
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                var data = await _bus.ReadDeviceInformationAsync(SlaveAddress, cancellationToken);
                ResultMessage = $"Device data: {ToHex(data)}";
            });
    }

    [RelayCommand(CanExecute = nameof(ProtocolEnabled))]
    private async Task CaptureDeviceInformationAsync()
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                const int durationMilliseconds = 3000;
                var slave = SlaveAddress;
                IsLogPaused = false;
                ResultMessage = "Capturing raw RX for 3 seconds; no response parsing.";
                AppendLog($"CAPTURE BEGIN  ADC {slave}, {_bus.PortName} | {_bus.BaudRate}, {durationMilliseconds} ms");
                var received = await _bus.CaptureDeviceInformationAsync(
                    slave,
                    durationMilliseconds,
                    cancellationToken);
                var request = AdcRtuFrame.Build(slave, AdcFunctionCode.RequestDeviceInformation, []);
                var summary = received.Length == 0
                    ? "No bytes received."
                    : received.AsSpan().StartsWith(request)
                        ? $"RX starts with the TX frame; {received.Length - request.Length} byte(s) follow it."
                        : "RX does not start with the TX frame.";
                ResultMessage = $"Captured {received.Length} bytes over {durationMilliseconds} ms. {summary}";
                AppendLog($"RX ALL (arrival order)  {ToHex(received)}");
                AppendLog($"CAPTURE END  ADC {slave}: {ResultMessage}");
            });
    }

    [RelayCommand(CanExecute = nameof(ProtocolEnabled))]
    private async Task ExecuteRegisterAsync()
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                var access = RegisterAccess;
                var address = ushort.Parse(AddressText);

                switch (access)
                {
                    case AdcFunctionCode.ReadHoldingRegisters:
                    case AdcFunctionCode.ReadInputRegisters:
                        RegisterResult = FormatRegisters(
                            address,
                            await _bus.ReadRegistersAsync(
                                SlaveAddress,
                                access,
                                address,
                                ushort.Parse(CountText),
                                cancellationToken));
                        break;
                    case AdcFunctionCode.WriteSingleRegister:
                        var value = ushort.Parse(ValueText);
                        if (address == (ushort)AdcRemoteRegister.RemoteStart && value != 0)
                        {
                            RegisterResult = "Raw Start is not available. Use Start Fastening or Reverse (Hold).";
                            return;
                        }

                        await _bus.WriteRegisterAsync(SlaveAddress, address, value, cancellationToken);
                        RegisterResult = $"{address} = {value} (0x{value:X4})";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            });
    }

    [RelayCommand]
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

    [RelayCommand]
    private void CopyLog()
    {
        CopyLogText(SelectedLogText.Length > 0 ? SelectedLogText : FrameLogText);
    }

    [RelayCommand]
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

    private async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operationCancellation = cancellation;
        try
        {
            _operation = _machine.RunAdcProtocolAsync(operation, cancellation.Token);
            RefreshControls();
            await ShowResultAsync(_operation);
        }
        finally
        {
            _operationCancellation = null;
            RefreshControls();
        }
    }

    private async Task ShowResultAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
            ResultMessage = "Operation canceled. Check controller status.";
        }
        catch (Exception exception)
        {
            ResultMessage = "Operation failed. Check controller status.";
            ConnectionStatus = exception.Message;
            _log?.Error("ADC diagnostic operation failed.", exception);
            AppendLog($"ERROR  {exception.Message}", record: false);
        }
    }

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
        var operation = _operation;
        if (operation.IsCompleted)
        {
            return;
        }

        _operationCancellation?.Cancel();
        await CommandShutdown.WaitAsync(operation);
    }

    [RelayCommand(CanExecute = nameof(PortSelectionEnabled))]
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

    private byte SlaveAddress
    {
        get
        {
            return byte.Parse(SlaveText);
        }
    }
}
