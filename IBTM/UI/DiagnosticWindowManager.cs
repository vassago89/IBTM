using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.UI;

// Owns WPF windows only; commands and device-operation lifetimes belong to view models.
public sealed class DiagnosticWindowManager
{
    private readonly IIoService _io;
    private readonly IoSignals _signals;
    private readonly HantasSettings _hantasSettings;
    private readonly MachineController _machine;
    private readonly MachineState _state;
    private readonly MotionDiagnosticsViewModel _motionViewModel;
    private readonly ApplicationLog _applicationLog;
    private readonly ILogger<DiagnosticWindowManager> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAdcBus _pickupAdcBus;
    private readonly IAdcBus _shootingAdcBus;
    private InputWindow? _input;
    private OutputWindow? _output;
    private MotionDiagnosticsWindow? _motion;
    private AdcProtocolWindow? _adc;
    private AdcProtocolViewModel? _adcViewModel;
    private LogWindow? _logs;
    private BoltStationTestWindow? _boltTest;
    private readonly BoltStationTestViewModel _boltTestViewModel;

    public DiagnosticWindowManager(
        IIoService io,
        IoSignals signals,
        HantasSettings hantasSettings,
        MachineController machine,
        MachineState state,
        MotionDiagnosticsViewModel motionViewModel,
        BoltStationTestViewModel boltTestViewModel,
        ApplicationLog applicationLog,
        ILoggerFactory loggerFactory,
        [FromKeyedServices(FasteningHead.Pickup)] IAdcBus pickupAdcBus,
        [FromKeyedServices(FasteningHead.Shooting)] IAdcBus shootingAdcBus)
    {
        _io = io;
        _signals = signals;
        _hantasSettings = hantasSettings;
        _machine = machine;
        _state = state;
        _motionViewModel = motionViewModel;
        _boltTestViewModel = boltTestViewModel;
        _applicationLog = applicationLog;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<DiagnosticWindowManager>();
        _pickupAdcBus = pickupAdcBus;
        _shootingAdcBus = shootingAdcBus;
    }

    public Window? Owner { private get; set; }

    public void OpenInputs()
    {
        if (_input is not null)
        {
            _input.Activate();
            return;
        }
        _input = new(new InputViewModel(_signals));
        _input.Closed += (_, _) => _input = null;
        ShowWindow(_input);
    }

    public void ShowStartConfirmation(OperationViewModel viewModel, CancellationToken cancellationToken)
    {
        var window = new StartConfirmationWindow(viewModel) { Owner = Owner };
        using var registration = cancellationToken.Register(() => window.Dispatcher.InvokeAsync(window.Close));
        if (!cancellationToken.IsCancellationRequested)
            window.ShowDialog();
    }

    public void OpenOutputs()
    {
        if (_state.AutoMode || _state.AutomaticRunning)
            return;
        if (_output is not null)
        {
            _output.Activate();
            return;
        }
        _output = new(new OutputViewModel(_signals, _machine));
        _output.Closed += (_, _) => _output = null;
        ShowWindow(_output);
    }

    public void CloseMaintenanceWindows()
    {
        _output?.Close();
        _motion?.Close();
        _adc?.Close();
        _boltTest?.Close();
    }

    public void OpenMotion()
    {
        if (_state.AutoMode || _state.AutomaticRunning)
            return;
        if (_motion is not null)
        {
            _motion.Activate();
            return;
        }
        _motion = new(_motionViewModel);
        _motion.Closed += (_, _) =>
        {
            _motion = null;
            _log.LogInformation("Motion monitor closed.");
        };
        ShowWindow(_motion);
        _log.LogInformation("Motion monitor opened.");
    }

    public void OpenAdcProtocol()
    {
        if (_state.AutoMode || _state.AutomaticRunning)
            return;
        if (_adc is not null)
        {
            _adc.Activate();
            return;
        }
        _adcViewModel = new(
            _pickupAdcBus,
            _shootingAdcBus,
            _io,
            _hantasSettings,
            _machine,
            _state,
            _loggerFactory.CreateLogger<AdcProtocolViewModel>(),
            _loggerFactory.CreateLogger<IBTM.Hantas.AdcBoltHead>());
        try
        {
            _adc = new(_adcViewModel);
        }
        catch
        {
            _adcViewModel.Dispose();
            _adcViewModel = null;
            throw;
        }
        _adc.Closed += (_, _) =>
        {
            _adc = null;
            _adcViewModel = null;
        };
        ShowWindow(_adc);
    }

    public void OpenBoltStationTest(Window? owner)
    {
        if (!_state.ManualMode || _state.AutomaticRunning)
            return;
        if (_boltTest is not null)
        {
            _boltTest.Activate();
            return;
        }
        _boltTest = new(_boltTestViewModel);
        _boltTest.Closed += (_, _) => _boltTest = null;
        ShowWindow(_boltTest, modal: true, owner: owner);
    }

    public void OpenLogs()
    {
        if (_logs is not null)
        {
            _logs.Activate();
            return;
        }
        var viewModel = new LogViewModel(_applicationLog);
        try
        {
            _logs = new(viewModel);
        }
        catch
        {
            viewModel.Dispose();
            throw;
        }
        _logs.Closed += (_, _) => _logs = null;
        ShowWindow(_logs);
    }

    private void ShowWindow(Window window, bool modal = false, Window? owner = null)
    {
        try
        {
            window.Owner = owner ?? Owner;
            window.ShowInTaskbar = false;
            if (modal)
                window.ShowDialog();
            else
                window.Show();
        }
        catch (Exception exception)
        {
            try
            {
                // Closing retains each window's cancellation/disposal and Closed handlers.
                window.Close();
            }
            catch (Exception closeException)
            {
                throw new AggregateException(UiText.Get("Diagnostic window could not be opened or closed."), exception, closeException);
            }
            throw;
        }
    }

    public void PrepareShutdown()
    {
        if (Owner is null)
            return;
        foreach (var window in Owner.OwnedWindows.Cast<Window>().ToArray())
        {
            window.IsEnabled = false;
            if (window is not AdcProtocolWindow and not OutputWindow and not MotionDiagnosticsWindow and not BoltStationTestWindow)
                window.Close();
        }
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.WaitAsync(
            _adcViewModel?.ShutdownAsync() ?? Task.CompletedTask,
            _boltTest is null ? Task.CompletedTask : _boltTestViewModel.ShutdownAsync(),
            _motion is null ? Task.CompletedTask : _motionViewModel.ShutdownAsync());
    }
}
