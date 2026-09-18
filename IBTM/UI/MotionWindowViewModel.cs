using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed class MotionMonitorAxis(
    MotionGroup group,
    MotionAxis axis,
    int number,
    MotionStatus motion,
    UnitSettings units,
    IRelayCommand<MotionMonitorAxis> toggleServoCommand,
    IAsyncRelayCommand<MotionMonitorAxis> homeAxisCommand) : ObservableObject
{
    private bool _lastEnabled = units.IsMotionEnabled(group);

    public IRelayCommand<MotionMonitorAxis> ToggleServoCommand { get; } = toggleServoCommand;
    public IAsyncRelayCommand<MotionMonitorAxis> HomeAxisCommand { get; } = homeAxisCommand;
    public MotionGroup Group { get; } = group;
    public MotionAxis Axis { get; } = axis;
    public int Number { get; } = number;

    public string Address
    {
        get
        {
            return Number.ToString("D3", CultureInfo.InvariantCulture);
        }
    }

    public bool Enabled
    {
        get
        {
            return units.IsMotionEnabled(Group);
        }
    }

    public MotionDiagnostics Diagnostics { get; } = motion.MonitorAxes[axis];

    internal bool RefreshEnabled()
    {
        return SetProperty(ref _lastEnabled, Enabled, nameof(Enabled));
    }
}

public partial class MotionWindowViewModel : ObservableObject
{
    private readonly MachineController _machine;
    private readonly MachineState _state;
    private Dispatcher? _dispatcher;
    private bool _active;
    private int _refreshQueued;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ControlsEnabled))]
    private bool _isClosing;
    [ObservableProperty]
    private string? _closeError;
    [ObservableProperty]
    private string _search = string.Empty;
    [ObservableProperty]
    private bool _enabledOnly = true;

    public MotionWindowViewModel(MachineController machine, MachineState state, MachineSettings settings)
    {
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
                            state.GetMotionStatus(section.Hardware.Group),
                            settings.Units,
                            ToggleServoCommand,
                            HomeAxisCommand)))
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

    public bool ControlsEnabled
    {
        get
        {
            return !IsClosing;
        }
    }

    public MotionMonitorAxis[] Axes { get; }
    public ICollectionView View { get; }

    public string ControlStatus
    {
        get
        {
            return !_state.Display.Available
                ? "Read only: machine status is unavailable."
                : _state.Display.AutoMode
                    ? "AUTO · monitoring only."
                    : _state.Display.IsRunning
                        ? "Operation in progress · monitoring remains available."
                        : "MANUAL";
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

    [RelayCommand(CanExecute = nameof(CanToggleServo))]
    private void ToggleServo(MotionMonitorAxis row)
    {
        _machine.ToggleServo(row.Group, row.Axis);
    }

    private bool CanToggleServo(MotionMonitorAxis? row)
    {
        return !IsClosing && row is { Diagnostics.Snapshot.State: not null }
            && _machine.CanSetServo(row.Group, live: false);
    }

    [RelayCommand(CanExecute = nameof(CanHomeAxis), IncludeCancelCommand = true)]
    private Task HomeAxisAsync(MotionMonitorAxis row, CancellationToken cancellationToken)
    {
        return Task.Run(() => _machine.HomeAxisAsync(row.Group, row.Axis, cancellationToken));
    }

    private bool CanHomeAxis(MotionMonitorAxis? row)
    {
        return !IsClosing && row is { Enabled: true }
            && _state.Display.Available
            && _state.Display.HomeableAxes.Contains((row.Group, row.Axis));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StopAsync()
    {
        try
        {
            await CommandShutdown.StopAsync(_machine.Stop, HomeAxisCommand);
        }
        catch (Exception exception)
        {
            if (!_state.IsError)
                _state.SetError(MachineAlarm.StopFailed, exception);
            else
                System.Diagnostics.Trace.TraceError("Motion window STOP also failed. {0}", exception);
        }
    }

    public void Refresh()
    {
        var enabledChanged = false;
        foreach (var row in Axes)
        {
            enabledChanged |= row.RefreshEnabled();
        }

        // Do not reset the list/scroll position on every feedback scan.
        if (enabledChanged)
            View.Refresh();
        OnPropertyChanged(nameof(ControlStatus));
        ToggleServoCommand.NotifyCanExecuteChanged();
        HomeAxisCommand.NotifyCanExecuteChanged();
    }

    public void Activate()
    {
        if (_active)
            return;
        _dispatcher = Dispatcher.CurrentDispatcher;
        IsClosing = false;
        _active = true;
        _state.DisplayChanged += OnDisplayChanged;
        Refresh();
        _state.RequestDisplayRefresh();
    }

    public void Deactivate()
    {
        _active = false;
        _state.DisplayChanged -= OnDisplayChanged;
    }

    private void OnDisplayChanged()
    {
        if (!_active || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
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
            System.Diagnostics.Trace.TraceError("Motion window shutdown failed. {0}", exception);
            return false;
        }
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.StopAsync(null, StopCommand, HomeAxisCommand);
    }
}
