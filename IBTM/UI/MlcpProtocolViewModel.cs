using System;
using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Mlcp;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class MlcpProtocolViewModel : ObservableObject, IDisposable
{
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly ILogger<MlcpProtocolViewModel> _log;
    private readonly IAsyncRelayCommand[] _commands;
    private readonly Lock _frameLogGate;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public MlcpProtocolViewModel(
        MlcpController controller,
        MachineState state,
        MachineController machine,
        ILogger<MlcpProtocolViewModel> log)
    {
        Controller = controller;
        _state = state;
        _machine = machine;
        _log = log;
        _frameLogGate = new();
        _dispatcher = Dispatcher.CurrentDispatcher;
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => IsConnectAllowed);
        MeasureCommand = new AsyncRelayCommand(MeasureAsync, () => IsRequestAllowed);
        ReadLastCommand = new AsyncRelayCommand(ReadLastAsync, () => IsRequestAllowed);
        CancelCommand = new RelayCommand(CancelRequest, () => MeasureCommand.IsRunning || ReadLastCommand.IsRunning);
        RefreshPortsCommand = new RelayCommand(RefreshPorts, () => IsConnectionSettingsEnabled);
        ClearLogCommand = new RelayCommand(ClearLog);
        _commands = [ToggleConnectionCommand, MeasureCommand, ReadLastCommand];
        PortNames = [];
        BaudRates = [9600, 19200, 38400, 57600, 115200];
        SelectedBaudRate = 9600;
        ResponseTimeoutText = controller.ResponseTimeout.TotalSeconds.ToString(CultureInfo.CurrentCulture);
        StatusMessage = UiText.Get("No result read");
        FrameLogText = "";
        foreach (var command in _commands)
            command.PropertyChanged += OnCommandChanged;
        controller.FrameTransferred += OnFrameTransferred;
        state.PropertyChanged += OnMachineStateChanged;
    }

    public MlcpController Controller { get; }
    public int[] BaudRates { get; }
    public IAsyncRelayCommand ToggleConnectionCommand { get; }
    public IAsyncRelayCommand MeasureCommand { get; }
    public IAsyncRelayCommand ReadLastCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand RefreshPortsCommand { get; }
    public IRelayCommand ClearLogCommand { get; }

    [ObservableProperty]
    public partial string[] PortNames { get; private set; }
    [ObservableProperty]
    public partial string? SelectedPort { get; set; }
    [ObservableProperty]
    public partial int SelectedBaudRate { get; set; }
    [ObservableProperty]
    public partial string ResponseTimeoutText { get; set; }
    [ObservableProperty]
    public partial MlcpResult? Result { get; private set; }
    [ObservableProperty]
    public partial DateTime? ResultReadAt { get; private set; }
    [ObservableProperty]
    public partial string StatusMessage { get; private set; }
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }
    [ObservableProperty]
    public partial string FrameLogText { get; private set; }
    [ObservableProperty]
    public partial bool IsClosing { get; set; }
    public string? CloseError { get; private set; }

    public bool IsBusy => _commands.Any(command => command.IsRunning);
    public bool IsConnectionSettingsEnabled => !_disposed && !IsClosing && !IsBusy && !Controller.IsOpen;
    public bool IsRequestSettingsEnabled => !_disposed && !IsClosing && !IsBusy && _state.ManualMode && !_state.IsRunning;
    public bool IsConnectAllowed => !_disposed && !IsClosing && !IsBusy
        && _state.ManualMode && !_state.IsRunning && (Controller.IsOpen || !string.IsNullOrWhiteSpace(SelectedPort));
    public bool IsRequestAllowed => !_disposed && !IsClosing && !IsBusy
        && Controller.IsOpen && _state.ManualMode && !_state.IsRunning;
    public string ConnectionAction => UiText.Get(Controller.IsOpen ? "Disconnect" : "Connect");

    partial void OnSelectedPortChanged(string? value)
    {
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsClosingChanged(bool value)
    {
        RefreshControls();
    }

    private async Task ToggleConnectionAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        try
        {
            using var operation = _machine.BeginManualOperation(
                () => _state.ManualMode && !_state.IsRunningFor(includeOperations: false), cancellationToken)
                ?? throw new InvalidOperationException(UiText.Get("Machine is busy"));
            operation.Token.ThrowIfCancellationRequested();
            if (Controller.IsOpen)
            {
                await Task.Run(Controller.Close);
                StatusMessage = UiText.Get("Disconnected");
            }
            else
            {
                var port = SelectedPort!;
                var baud = SelectedBaudRate;
                await Task.Run(() => Controller.Open(port, baud));
                StatusMessage = $"{port} | {baud} | 8N1";
                Result = null;
                ResultReadAt = null;
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = UiText.Get(exception);
            _log.LogError(exception, "LVDT diagnostic connection failed.");
        }
        finally
        {
            RefreshControls();
        }
    }

    private Task MeasureAsync(CancellationToken cancellationToken)
    {
        return ReadMeasurementAsync(false, cancellationToken);
    }

    private Task ReadLastAsync(CancellationToken cancellationToken)
    {
        return ReadMeasurementAsync(true, cancellationToken);
    }

    private async Task ReadMeasurementAsync(bool recall, CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        Result = null;
        ResultReadAt = null;
        StatusMessage = UiText.Get("Waiting for response");
        try
        {
            using var operation = _machine.BeginManualOperation(
                () => _state.ManualMode && !_state.IsRunningFor(includeOperations: false), cancellationToken)
                ?? throw new InvalidOperationException(UiText.Get("Machine is busy"));
            operation.Token.ThrowIfCancellationRequested();
            if (!double.TryParse(ResponseTimeoutText, out var seconds)
                || !double.IsFinite(seconds) || seconds <= 0 || seconds > int.MaxValue / 1000d)
                throw new ArgumentException(UiText.Get("Response timeout must be greater than 0"));
            Controller.ResponseTimeout = TimeSpan.FromSeconds(seconds);
            Result = recall
                ? await Controller.ReadLastMeasurementAsync(operation.Token)
                : await Controller.MeasureAsync(operation.Token);
            ResultReadAt = DateTime.Now;
            StatusMessage = UiText.Get(recall ? "Last measurement received" : "Measurement received");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = UiText.Get("Response wait cancelled");
        }
        catch (Exception exception)
        {
            StatusMessage = UiText.Get("No result read");
            ErrorMessage = UiText.Get(exception);
            _log.LogError(exception, "LVDT diagnostic {Command} failed.", recall ? "RECALL" : "START");
        }
        finally
        {
            RefreshControls();
        }
    }

    private void CancelRequest()
    {
        MeasureCommand.Cancel();
        ReadLastCommand.Cancel();
    }

    private void RefreshPorts()
    {
        try
        {
            PortNames = SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            SelectedPort ??= PortNames.FirstOrDefault();
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            ErrorMessage = UiText.Get(exception);
            _log.LogError(exception, "LVDT serial port enumeration failed.");
        }
    }

    private void OnFrameTransferred(MlcpFrameDirection direction, byte[] bytes)
    {
        var text = new StringBuilder();
        foreach (var value in bytes)
        {
            text.Append(value switch
            {
                0x05 => "<ENQ>",
                0x02 => "<STX>",
                0x03 => "<ETX>",
                0x0D => "\\r",
                0x0A => "\\n",
                >= 0x20 and <= 0x7E => ((char)value).ToString(),
                _ => $"\\x{value:X2}",
            });
        }
        var line = $"{DateTime.Now:HH:mm:ss.fff} {(direction == MlcpFrameDirection.Transmit ? "TX" : "RX")}  {text}\n"
            + $"    {Convert.ToHexString(bytes)}\n";
        lock (_frameLogGate)
        {
            FrameLogText += line;
            if (FrameLogText.Length > 32768)
                FrameLogText = FrameLogText[^32768..];
        }
    }

    private void ClearLog()
    {
        lock (_frameLogGate)
            FrameLogText = "";
    }

    private void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            RefreshControls();
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshControls();
    }

    private void RefreshControls()
    {
        if (_disposed)
            return;
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(RefreshControls);
            return;
        }
        OnPropertyChanged(nameof(Controller));
        OnPropertyChanged(nameof(ConnectionAction));
        OnPropertyChanged(nameof(IsConnectionSettingsEnabled));
        OnPropertyChanged(nameof(IsRequestSettingsEnabled));
        foreach (var command in _commands)
            command.NotifyCanExecuteChanged();
        RefreshPortsCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public async Task<bool> TryCloseAsync()
    {
        IsClosing = true;
        CloseError = null;
        try
        {
            await ShutdownAsync();
            return true;
        }
        catch (Exception exception)
        {
            IsClosing = false;
            CloseError = UiText.Get(exception);
            _log.LogError(exception, "LVDT diagnostic shutdown failed.");
            return false;
        }
    }

    internal async Task ShutdownAsync()
    {
        await CommandShutdown.CancelAndWaitAsync(_commands);
        RefreshControls();
    }

    public void Dispose()
    {
        _disposed = true;
        Controller.FrameTransferred -= OnFrameTransferred;
        _state.PropertyChanged -= OnMachineStateChanged;
        foreach (var command in _commands)
            command.PropertyChanged -= OnCommandChanged;
    }
}
