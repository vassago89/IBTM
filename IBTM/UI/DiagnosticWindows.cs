using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

// Owns WPF windows only; commands and device-operation lifetimes belong to view models.
public sealed class DiagnosticWindows
{
    private readonly IIoService _io;
    private readonly IoSignals _signals;
    private readonly HantasSettings _hantasSettings;
    private readonly MachineController _machine;
    private readonly MachineState _state;
    private readonly MotionWindowViewModel _motionViewModel;
    private readonly ApplicationLog _applicationLog;
    private readonly ILogger<DiagnosticWindows> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAdcBus? _adcBus;
    private InputWindow? _input;
    private OutputWindow? _output;
    private MotionWindow? _motion;
    private AdcProtocolWindow? _adc;
    private AdcProtocolViewModel? _adcViewModel;
    private LogWindow? _logs;

    public DiagnosticWindows(
        IIoService io,
        IoSignals signals,
        HantasSettings hantasSettings,
        MachineController machine,
        MachineState state,
        MotionWindowViewModel motionViewModel,
        ApplicationLog applicationLog,
        ILoggerFactory loggerFactory,
        IAdcBus? adcBus = null)
    {
        _io = io;
        _signals = signals;
        _hantasSettings = hantasSettings;
        _machine = machine;
        _state = state;
        _motionViewModel = motionViewModel;
        _applicationLog = applicationLog;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<DiagnosticWindows>();
        _adcBus = adcBus;
    }

    public Window? Owner { private get; set; }

    public void OpenInputs()
    {
        if (_input is not null)
        {
            _input.Activate();
            return;
        }
        _input = new(new InputWindowViewModel(_io, _signals)) { Owner = Owner };
        _input.Closed += (_, _) => _input = null;
        _input.Show();
    }

    public void OpenOutputs()
    {
        if (_output is not null)
        {
            _output.Activate();
            return;
        }
        _output = new(new OutputWindowViewModel(_signals, _machine)) { Owner = Owner };
        _output.Closed += (_, _) => _output = null;
        _output.Show();
    }

    public void CloseOutputs()
    {
        _output?.Close();
    }

    public void OpenMotion()
    {
        if (_motion is not null)
        {
            _motion.Activate();
            return;
        }
        _motion = new(_motionViewModel) { Owner = Owner };
        _motion.Closed += (_, _) =>
        {
            _motion = null;
            _log.LogInformation("Motion monitor closed.");
        };
        _motion.Show();
        _log.LogInformation("Motion monitor opened.");
    }

    public void OpenAdcProtocol()
    {
        if (_adc is not null)
        {
            _adc.Activate();
            return;
        }
        _adcViewModel = new(
            _adcBus ?? throw new System.InvalidOperationException("ADC diagnostics are unavailable for IO-only bolt controllers."),
            _hantasSettings,
            _machine,
            _state,
            _loggerFactory.CreateLogger<AdcProtocolViewModel>());
        _adc = new(_adcViewModel) { Owner = Owner };
        _adc.Closed += (_, _) =>
        {
            _adc = null;
            _adcViewModel = null;
        };
        _adc.Show();
    }

    public void OpenLogs()
    {
        if (_logs is not null)
        {
            _logs.Activate();
            return;
        }
        _logs = new(new LogWindowViewModel(_applicationLog)) { Owner = Owner };
        _logs.Closed += (_, _) => _logs = null;
        _logs.Show();
    }

    public void PrepareShutdown()
    {
        if (Owner is null)
            return;
        foreach (var window in Owner.OwnedWindows.Cast<Window>().ToArray())
        {
            window.IsEnabled = false;
            if (window is not AdcProtocolWindow and not OutputWindow and not MotionWindow)
                window.Close();
        }
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.WaitAsync(
            _adcViewModel?.ShutdownAsync() ?? Task.CompletedTask,
            _motion is null ? Task.CompletedTask : _motionViewModel.ShutdownAsync());
    }
}
