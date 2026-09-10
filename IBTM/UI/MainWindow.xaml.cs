using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;

namespace IBTM.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IIoService _io;
    private readonly IoSignals _signals;
    private readonly IAdcBus _adcBus;
    private readonly HantasSettings _hantasSettings;
    private readonly MachineController _machine;
    private readonly MachineState _state;
    private InputWindow? _inputWindow;
    private OutputWindow? _outputWindow;
    private MotionWindow? _motionWindow;
    private readonly MotionWindowViewModel _motionViewModel;
    private AdcProtocolWindow? _adcProtocolWindow;
    private readonly ApplicationLog _log;
    private LogWindow? _logWindow;
    private bool _closing;
    private bool _closeApproved;

    public MainWindow(
        MainViewModel viewModel,
        IIoService io,
        IoSignals signals,
        IAdcBus adcBus,
        HantasSettings hantasSettings,
        MachineController machine,
        MachineState state,
        MotionWindowViewModel motionViewModel,
        ApplicationLog? log = null)
    {
        _viewModel = viewModel;
        _io = io;
        _signals = signals;
        _adcBus = adcBus;
        _hantasSettings = hantasSettings;
        _machine = machine;
        _state = state;
        _motionViewModel = motionViewModel;
        _log = log ?? new ApplicationLog();
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeApproved)
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
        IsEnabled = false;
        foreach (var window in OwnedWindows.Cast<Window>().ToArray())
        {
            window.IsEnabled = false;
            if (window is not AdcProtocolWindow and not OutputWindow and not MotionWindow)
            {
                window.Close();
            }
        }

        try
        {
            await CommandShutdown.WaitAsync(
                _machine.ShutdownAsync(),
                _adcProtocolWindow?.StopAsync() ?? Task.CompletedTask,
                _motionWindow?.ShutdownAsync() ?? Task.CompletedTask,
                _viewModel.ShutdownAsync());
            _closeApproved = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            _log.Error("Main window shutdown failed.", exception);
            var errors = exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.Select(error => error.Message).Distinct()
                : [exception.Message];
            var result = MessageBox.Show(
                this,
                "Device stop or shutdown could not be confirmed.\n"
                    + "Check that the equipment is safely stopped before exiting.\n\n"
                    + string.Join("\n", errors)
                    + "\n\nExit the application anyway? Full details are saved in the log.",
                "Shutdown Incomplete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result == MessageBoxResult.Yes)
            {
                _log.Write("Operator approved application exit after shutdown failure; device stop is unconfirmed.");
                _closeApproved = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
            else
            {
                _closing = false;
                IsEnabled = true;
            }
        }
    }

    private void OnOpenInputs(object sender, RoutedEventArgs e)
    {
        if (_inputWindow is not null)
        {
            _inputWindow.Activate();
            return;
        }

        _inputWindow = new InputWindow(_io, _signals)
        {
            Owner = this,
        };
        _inputWindow.Closed += (_, _) => _inputWindow = null;
        _inputWindow.Show();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        if (_logWindow is not null)
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow(_log) { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    private void OnOpenOutputs(object sender, RoutedEventArgs e)
    {
        // Recheck the selector when clicked, before the next display update arrives.
        if (_closing || !_state.ManualMode || !_viewModel.OutputsWindowEnabled)
            return;

        if (_outputWindow is not null)
        {
            _outputWindow.Activate();
            return;
        }

        _outputWindow = new OutputWindow(_signals, _machine, _state)
        {
            Owner = this,
        };
        _outputWindow.Closed += (_, _) => _outputWindow = null;
        _outputWindow.Show();
    }

    private void OnOpenMotion(object sender, RoutedEventArgs e)
    {
        if (_motionWindow is not null)
        {
            _motionWindow.Activate();
            return;
        }

        _motionWindow = new MotionWindow(_motionViewModel, _state) { Owner = this };
        _motionWindow.Closed += (_, _) =>
        {
            _motionWindow = null;
            _log.Write("Motion monitor closed.");
        };
        _motionWindow.Show();
        _log.Write("Motion monitor opened.");
    }

    private void OnOpenAdcProtocol(object sender, RoutedEventArgs e)
    {
        if (_adcProtocolWindow is not null)
        {
            _adcProtocolWindow.Activate();
            return;
        }

        _adcProtocolWindow = new AdcProtocolWindow(_adcBus, _hantasSettings, _machine, _log)
        {
            Owner = this,
        };
        _adcProtocolWindow.Closed += (_, _) => _adcProtocolWindow = null;
        _adcProtocolWindow.Show();
    }

    private void RecipeFile_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string recipeName)
        {
            _viewModel.RecipeEditor.LoadCommand.Execute(recipeName);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closing || sender is not MainViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.OutputsWindowEnabled)
            && !viewModel.OutputsWindowEnabled)
        {
            _outputWindow?.Close();
        }

        if (e.PropertyName == nameof(MainViewModel.AdcProtocolEnabled))
        {
            if (!viewModel.AdcProtocolEnabled)
            {
                _adcProtocolWindow?.Close();
            }
            else
            {
                _adcProtocolWindow?.RefreshControls();
            }
        }
    }
}
