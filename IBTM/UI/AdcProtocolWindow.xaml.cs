using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using IBTM.Device;
using IBTM.Hantas;

namespace IBTM.UI;

public partial class AdcProtocolWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IAdcBus _bus;

    public AdcProtocolWindow(
        IAdcBus bus,
        HantasSettings settings)
    {
        _bus = bus;
        InitializeComponent();
        DataContext = this;
        RefreshPorts();
        BaudBox.SelectedItem = settings.BaudRate;
        SlaveBox.Text = settings.PickupSlaveAddress.ToString();
        AccessBox.SelectedItem = AdcRegisterAccess.ReadInputRegisters;
        AddressBox.Text = ((ushort)AdcResultRegister.EventCount).ToString();
        CountBox.Text = AdcFasteningResult.RegisterCount.ToString();
        _bus.FrameTransferred += OnFrameTransferred;
        if (_bus.IsOpen)
        {
            PortBox.SelectedItem = _bus.PortName;
            BaudBox.SelectedItem = _bus.BaudRate;
            ConnectButton.Content = "Disconnect";
            ConnectionStatusText.Text =
                $"{_bus.PortName} | {_bus.BaudRate}";
        }
        SetBusy(false);
    }

    public int[] BaudRates { get; } = [9600, 19200, 38400, 57600, 115200];
    public AdcRegisterAccess[] RegisterAccesses { get; } =
        Enum.GetValues<AdcRegisterAccess>();

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _bus.FrameTransferred -= OnFrameTransferred;
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void OnRefreshPorts(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void OnToggleConnection(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(() =>
        {
            if (_bus.IsOpen)
            {
                _bus.Close();
                ConnectButton.Content = "Connect";
                ConnectionStatusText.Text = "Disconnected";
                return Task.CompletedTask;
            }

            var portName = (string)PortBox.SelectedItem;
            var baudRate = (int)BaudBox.SelectedItem;
            _bus.Open(portName, baudRate);
            ConnectButton.Content = "Disconnect";
            ConnectionStatusText.Text =
                $"{portName} | {baudRate}";
            return Task.CompletedTask;
        });

    private async void OnSelectPreset(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () =>
        {
            var preset = ushort.Parse(PresetBox.Text);
            await _bus.SelectPresetAsync(
                SlaveAddress,
                preset,
                _lifetime.Token);
            ResultText.Text = $"Preset {preset} selected";
        });

    private async void OnStart(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () =>
        {
            await _bus.SetDirectionAsync(
                SlaveAddress,
                AdcDirection.Fastening,
                _lifetime.Token);
            await _bus.StartAsync(SlaveAddress, _lifetime.Token);
            ResultText.Text = "Fastening started";
        });

    private async void OnStop(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () =>
        {
            await _bus.StopAsync(SlaveAddress, _lifetime.Token);
            ResultText.Text = "Stopped";
        });

    private async void OnResetAlarm(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () =>
        {
            await _bus.ResetAlarmAsync(SlaveAddress, _lifetime.Token);
            ResultText.Text = "Alarm reset sent";
        });

    private async void OnReadResult(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () =>
        {
            var result = await _bus.ReadFasteningResultAsync(
                SlaveAddress,
                _lifetime.Token);
            ResultText.Text =
                $"{result.Status}  Event {result.EventCount}\n" +
                $"Preset {result.Preset}  Torque {result.Torque:F2} / {result.TargetTorque:F2}\n" +
                $"Time {result.FasteningTimeMilliseconds} ms  Error {result.Error}";
        });

    private async void OnReadDeviceInformation(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () =>
        {
            var data = await _bus.ReadDeviceInformationAsync(
                SlaveAddress,
                _lifetime.Token);
            ResultText.Text = $"Device data: {ToHex(data)}";
        });

    private async void OnExecuteRegister(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () =>
        {
            var access = (AdcRegisterAccess)AccessBox.SelectedItem;
            var address = ushort.Parse(AddressBox.Text);

            switch (access)
            {
                case AdcRegisterAccess.ReadHoldingRegisters:
                    RegisterResultText.Text = FormatRegisters(
                        address,
                        await _bus.ReadHoldingRegistersAsync(
                            SlaveAddress,
                            address,
                            ushort.Parse(CountBox.Text),
                            _lifetime.Token));
                    break;
                case AdcRegisterAccess.ReadInputRegisters:
                    RegisterResultText.Text = FormatRegisters(
                        address,
                        await _bus.ReadInputRegistersAsync(
                            SlaveAddress,
                            address,
                            ushort.Parse(CountBox.Text),
                            _lifetime.Token));
                    break;
                case AdcRegisterAccess.WriteSingleRegister:
                    var value = ushort.Parse(ValueBox.Text);
                    await _bus.WriteRegisterAsync(
                        SlaveAddress,
                        address,
                        value,
                        _lifetime.Token);
                    RegisterResultText.Text = $"{address} = {value} (0x{value:X4})";
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        });

    private void OnClearLog(object sender, RoutedEventArgs e) => LogBox.Clear();

    private async Task ExecuteAsync(Func<Task> operation)
    {
        SetBusy(true);
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ConnectionStatusText.Text = exception.Message;
            AppendLog($"ERROR  {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ConnectionControls.IsEnabled = !busy;
        var connected = _bus.IsOpen;
        PortBox.IsEnabled = !busy && !connected;
        BaudBox.IsEnabled = !busy && !connected;
        SlaveBox.IsEnabled = !busy;
        RefreshPortsButton.IsEnabled = !busy && !connected;
        ConnectButton.IsEnabled = !busy
            && (connected || PortBox.SelectedItem is string);
        OperationPanel.IsEnabled = !busy && connected;
        RegisterPanel.IsEnabled = !busy && connected;
    }

    private void RefreshPorts()
    {
        var selected = PortBox.SelectedItem as string;
        var ports = _bus.GetPortNames();
        PortBox.ItemsSource = ports;
        PortBox.SelectedItem = ports.Contains(selected) ? selected : ports.FirstOrDefault();
        ConnectButton.IsEnabled = ports.Length > 0;
        if (ports.Length == 0)
        {
            ConnectionStatusText.Text = "No serial ports found";
        }
    }

    private void OnFrameTransferred(AdcFrameDirection direction, byte[] frame) =>
        Dispatcher.BeginInvoke(() =>
            AppendLog($"{(direction == AdcFrameDirection.Transmit ? "TX" : "RX")}     {ToHex(frame)}"));

    private void AppendLog(string text)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {text}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private static string FormatRegisters(ushort address, ushort[] values) =>
        string.Join(
            Environment.NewLine,
            values.Select((value, index) =>
                $"{address + index} = {value} (0x{value:X4})"));

    private static string ToHex(byte[] data) =>
        string.Join(' ', data.Select(value => value.ToString("X2")));

    private byte SlaveAddress => byte.Parse(SlaveBox.Text);
}
