using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;

namespace IBTM.UI;

[INotifyPropertyChanged]
public partial class AdcProtocolWindow : Window
{
    private const int MaximumLogEntries = 1000;
    private readonly object _frameLogGate = new();
    private readonly ObservableCollection<string> _frameLog = new();

    private readonly CancellationTokenSource _lifetime = new();
    private readonly IAdcBus _bus;
    private readonly HantasSettings _settings;
    private readonly MachineController _machine;
    private readonly ApplicationLog? _log;
    private CancellationTokenSource? _operationCancellation;
    private Task _operation = Task.CompletedTask;
    private bool _closing;

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

    public AdcProtocolWindow(
        IAdcBus bus,
        HantasSettings settings,
        MachineController machine,
        ApplicationLog? log = null)
    {
        _bus = bus;
        _settings = settings;
        _machine = machine;
        _log = log;
        FrameLog = new ReadOnlyObservableCollection<string>(_frameLog);
        BindingOperations.EnableCollectionSynchronization(FrameLog, _frameLogGate);
        ReverseCommand = new AsyncRelayCommand(
            token => ExecuteAsync(TestReverseAsync, token),
            CanReverse);
        ReleaseReverseCommand = new RelayCommand(ReverseCommand.Cancel);
        InitializeComponent();
        DataContext = this;
        RefreshPorts();
        BaudBox.SelectedItem = settings.BaudRate;
        SlaveBox.Text = settings.PickupSlaveAddress.ToString();
        AccessBox.SelectedItem = AdcFunctionCode.ReadInputRegisters;
        AddressBox.Text = ((ushort)AdcResultRegister.EventCount).ToString();
        CountBox.Text = AdcFasteningResult.RegisterCount.ToString();
        NextResultBox.SelectedItem = AdcEventStatus.FasteningOk;
        _bus.FrameTransferred += OnFrameTransferred;
        if (_bus.IsOpen)
        {
            PortBox.SelectedItem = _bus.PortName;
            BaudBox.SelectedItem = _bus.BaudRate;
            ConnectionAction = "Disconnect";
            ConnectionStatus = $"{_bus.PortName} | {_bus.BaudRate}";
        }

        SetBusy(false);
    }

    public int[] BaudRates { get; } = [9600, 19200, 38400, 57600, 115200];
    public ReadOnlyObservableCollection<string> FrameLog { get; }
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

    public IAsyncRelayCommand ReverseCommand { get; }
    public IRelayCommand ReleaseReverseCommand { get; }
    public AdcEventStatus[] FasteningResults { get; } = [AdcEventStatus.FasteningOk, AdcEventStatus.FasteningNg, AdcEventStatus.Error,];

    private void OnQueueResult(object sender, RoutedEventArgs e)
    {
        if (_bus is not VirtualAdcBus virtualBus)
        {
            return;
        }

        if (!byte.TryParse(SlaveBox.Text, out var slave))
        {
            ConnectionStatus = "Enter a valid slave address";
            return;
        }

        var result = (AdcEventStatus)NextResultBox.SelectedItem;
        virtualBus.SetNextFasteningResult(slave, result);
        AppendLog($"VIRTUAL  Slave {slave}: {result.GetDescription()} set for one fastening");
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_operation.IsCompleted)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_closing)
        {
            return;
        }

        _closing = true;
        _operationCancellation?.Cancel();
        try
        {
            await CommandShutdown.WaitAsync(_operation);
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            _closing = false;
            SetBusy(false);
            _log?.Error("ADC diagnostic shutdown failed.", exception);
            MessageBox.Show(
                this,
                exception.Message,
                "ADC Shutdown Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _bus.FrameTransferred -= OnFrameTransferred;
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void OnRefreshPorts(object sender, RoutedEventArgs e)
    {
        RefreshPorts();
    }

    private void OnPortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshControls();
    }

    private async void OnToggleConnection(object sender, RoutedEventArgs e)
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

                var portName = (string)PortBox.SelectedItem;
                var baudRate = (int)BaudBox.SelectedItem;
                await Task.Run(() => _bus.Open(portName, baudRate), cancellationToken);
                ConnectionAction = "Disconnect";
                ConnectionStatus = $"{portName} | {baudRate}";
                AppendLog($"CONNECT  {_bus.PortName} | {_bus.BaudRate}");
            });
    }

    private async void OnSelectPreset(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                var preset = ushort.Parse(PresetBox.Text);
                await _bus.SelectPresetAsync(SlaveAddress, preset, cancellationToken);
                ResultMessage = $"Preset {preset} selected";
            });
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(TestFasteningAsync);
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (!_operation.IsCompleted)
        {
            _operationCancellation!.Cancel();
            await ShowResultAsync(_operation);
            return;
        }

        await ExecuteAsync(
            async cancellationToken =>
            {
                await _bus.StopAsync(SlaveAddress, cancellationToken);
                ResultMessage = "Stopped";
            });
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

    private bool CanReverse()
    {
        return !_closing
            && _bus.IsOpen
            && _machine.CanUseAdcProtocol
            && _machine.CanTestBoltHead;
    }

    private async void OnResetAlarm(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                await _bus.ResetAlarmAsync(SlaveAddress, cancellationToken);
                ResultMessage = "Alarm reset sent";
            });
    }

    private async void OnReadResult(object sender, RoutedEventArgs e)
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

    private async void OnReadDeviceInformation(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                var data = await _bus.ReadDeviceInformationAsync(SlaveAddress, cancellationToken);
                ResultMessage = $"Device data: {ToHex(data)}";
            });
    }

    private async void OnExecuteRegister(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(
            async cancellationToken =>
            {
                var access = (AdcFunctionCode)AccessBox.SelectedItem;
                var address = ushort.Parse(AddressBox.Text);

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
                                ushort.Parse(CountBox.Text),
                                cancellationToken));
                        break;
                    case AdcFunctionCode.WriteSingleRegister:
                        var value = ushort.Parse(ValueBox.Text);
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

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        lock (_frameLogGate)
            _frameLog.Clear();
    }

    private Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        if (!_machine.CanUseAdcProtocol || !_operation.IsCompleted || _closing)
        {
            return Task.CompletedTask;
        }

        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        SetBusy(true);
        _operation = ExecuteCoreAsync(operation, _operationCancellation.Token);
        return ShowResultAsync(_operation);
    }

    private async Task ExecuteCoreAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _machine.RunAdcProtocolAsync(operation, cancellationToken);
        }
        finally
        {
            _operationCancellation!.Dispose();
            _operationCancellation = null;
            SetBusy(false);
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
            ResultMessage = "Stopped";
        }
        catch (Exception exception)
        {
            ResultMessage = "Operation failed. Check controller status.";
            ConnectionStatus = exception.Message;
            _log?.Error("ADC diagnostic operation failed.", exception);
            AppendLog($"ERROR  {exception.Message}", record: false);
        }
    }

    public void RefreshControls()
    {
        SetBusy(!_operation.IsCompleted);
    }

    internal async Task StopAsync()
    {
        var operation = _operation;
        if (operation.IsCompleted)
        {
            return;
        }

        _operationCancellation?.Cancel();
        await CommandShutdown.WaitAsync(operation);
    }

    private void SetBusy(bool busy)
    {
        busy |= _closing;
        ConnectionControls.IsEnabled = !busy;
        var protocolEnabled = !busy && _machine.CanUseAdcProtocol;
        var connected = _bus.IsOpen;
        PortBox.IsEnabled = protocolEnabled && !connected;
        BaudBox.IsEnabled = protocolEnabled && !connected;
        SlaveBox.IsEnabled = !busy && (IsVirtual || _machine.CanUseAdcProtocol);
        RefreshPortsButton.IsEnabled = protocolEnabled && !connected;
        ConnectButton.IsEnabled = protocolEnabled && (connected || PortBox.SelectedItem is string);
        OperationPanel.IsEnabled = protocolEnabled && connected;
        RegisterPanel.IsEnabled = protocolEnabled && connected;
        StartButton.IsEnabled = protocolEnabled && connected && _machine.CanTestBoltHead;
        ReverseCommand.NotifyCanExecuteChanged();
        StopButton.IsEnabled = !_closing
            && (!_operation.IsCompleted || connected && _machine.CanUseAdcProtocol);
        VirtualResultPanel.IsEnabled = !_closing;
    }

    private void RefreshPorts()
    {
        var selected = PortBox.SelectedItem as string ?? _settings.PortName;
        var ports = _bus.GetPortNames();
        PortNames = ports;
        PortBox.SelectedItem = ports.FirstOrDefault(
            port => string.Equals(port, selected, StringComparison.OrdinalIgnoreCase));
        if (!_bus.IsOpen)
        {
            ConnectionStatus = ports.Length == 0
                ? "No serial ports found"
                : PortBox.SelectedItem is null ? "Select a serial port" : "Disconnected";
        }

        SetBusy(!_operation.IsCompleted);
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
            return byte.Parse(SlaveBox.Text);
        }
    }
}
