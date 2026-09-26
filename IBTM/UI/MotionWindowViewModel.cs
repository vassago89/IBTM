using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class MotionWindowViewModel : ObservableObject
{
    private readonly ILogger<MotionWindowViewModel>? _log;
    private readonly MachineController _machine;
    private readonly MachineState _state;
    private Dispatcher? _dispatcher;
    private bool _active;
    private int _refreshQueued;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ControlsEnabled))]
    public partial bool IsClosing { get; set; }
    [ObservableProperty]
    public partial string? CloseError { get; set; }
    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool EnabledOnly { get; set; } = true;

    public MotionWindowViewModel(
        MachineController machine, MachineState state, MachineSettings settings,
        ILogger<MotionWindowViewModel>? log = null)
    {
        StopCommand = new AsyncRelayCommand(StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);

        _log = log;
        _machine = machine;
        _state = state;
        // Capture the running application's axis numbers, not later unsaved mapping edits.
        Axes = settings.MotionSections.SelectMany(
            section =>
                section.Hardware.AxisSignals.Select(
                    axis =>
                        new MotionMonitorAxis(
                            section.Hardware.Group,
                            axis.Key,
                            section.Hardware.Axes[axis.Value].Number,
                            machine,
                            state,
                            settings.Units)))
            .ToArray();
        View = new ListCollectionView(Axes);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MotionMonitorAxis.Group)));
        View.Filter = item =>
            item is MotionMonitorAxis row
                && (!EnabledOnly || row.Enabled)
                && (string.IsNullOrWhiteSpace(Search)
                    || $"{row.Address} {row.Group.GetDescription()} {row.Axis}".Contains(
                        Search.Trim(),
                        StringComparison.OrdinalIgnoreCase));
    }

    public bool ControlsEnabled => !IsClosing;

    public MotionMonitorAxis[] Axes { get; }
    public ICollectionView View { get; }

    public string ControlStatus
    {
        get
        {
            if (!_state.Available)
                return "Read only: machine status is unavailable.";
            if (_state.AutoMode)
                return "AUTO · monitoring only.";
            if (_state.IsRunning)
                return "Operation in progress · monitoring remains available.";
            return "MANUAL";
        }
    }

    partial void OnSearchChanged(string value)
    {
        View.Refresh();
    }

    partial void OnEnabledOnlyChanged(bool value)
    {
        View.Refresh();
    }

    public IAsyncRelayCommand StopCommand { get; }

    private async Task StopAsync()
    {
        try
        {
            var commands = Axes.Select(axis => axis.HomeCommand).ToArray();
            var pending = CommandShutdown.Capture(commands);
            await CommandShutdown.CancelAndWaitAsync(
                commands,
                _machine.StopAsync(),
                pending);
        }
        catch (Exception exception)
        {
            if (!_state.IsError)
                _state.SetError(MachineAlarm.StopFailed, exception);
            else
                _log?.LogError(exception, "Motion window STOP also failed.");
        }
    }

    public void Refresh()
    {
        var enabledChanged = false;
        foreach (var row in Axes)
        {
            enabledChanged |= row.Refresh();
        }

        // Do not reset the list/scroll position on every feedback scan.
        if (enabledChanged)
            View.Refresh();
        OnPropertyChanged(nameof(ControlStatus));
    }

    public void Activate()
    {
        if (_active)
            return;
        _dispatcher = Dispatcher.CurrentDispatcher;
        IsClosing = false;
        _active = true;
        _state.PropertyChanged += OnDisplayChanged;
        foreach (var row in Axes)
        {
            row.Diagnostics.PropertyChanged += OnDisplayChanged;
            _state.GetMotionStatus(row.Group).Axes[row.Axis].PropertyChanged += OnDisplayChanged;
        }
        Refresh();
    }

    public void Deactivate()
    {
        _active = false;
        _state.PropertyChanged -= OnDisplayChanged;
        foreach (var row in Axes)
        {
            row.Diagnostics.PropertyChanged -= OnDisplayChanged;
            _state.GetMotionStatus(row.Group).Axes[row.Axis].PropertyChanged -= OnDisplayChanged;
        }
    }

    private void OnDisplayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AxisStatus && e.PropertyName != nameof(AxisStatus.State)
            || !_active || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        _dispatcher!.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_active && !IsClosing)
                Refresh();
        });
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
            CloseError = exception.Message;
            _log?.LogError(exception, "Motion window shutdown failed.");
            return false;
        }
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.CancelAndWaitAsync([StopCommand, .. Axes.Select(axis => axis.HomeCommand)]);
    }
}
