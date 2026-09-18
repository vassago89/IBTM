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

namespace IBTM.UI;

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
        StopCommand = new AsyncRelayCommand(StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);

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
            if (!_state.Display.Available)
            {
                return "Read only: machine status is unavailable.";
            }

            if (_state.Display.AutoMode)
            {
                return "AUTO · monitoring only.";
            }

            if (_state.Display.IsRunning)
            {
                return "Operation in progress · monitoring remains available.";
            }

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
                System.Diagnostics.Trace.TraceError("Motion window STOP also failed. {0}", exception);
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
        return CommandShutdown.CancelAndWaitAsync([StopCommand, .. Axes.Select(axis => axis.HomeCommand)]);
    }
}
