using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IBTM.Device;
using IBTM.Hantas;

namespace IBTM.UI;

public partial class MainWindow : Window
{
    private readonly IIoService _io;
    private readonly IReadOnlyDictionary<InputIo, HardwareArea> _inputAreas;
    private readonly IReadOnlyDictionary<OutputIo, HardwareArea> _outputAreas;
    private readonly IAdcBus _adcBus;
    private readonly HantasSettings _hantasSettings;
    private readonly MachineController _machine;
    private InputWindow? _inputWindow;
    private OutputWindow? _outputWindow;
    private AdcProtocolWindow? _adcProtocolWindow;
    private bool _closing;
    private bool _shutdownCompleted;

    public MainWindow(
        MainViewModel viewModel,
        IIoService io,
        IReadOnlyDictionary<InputIo, HardwareArea> inputAreas,
        IReadOnlyDictionary<OutputIo, HardwareArea> outputAreas,
        IAdcBus adcBus,
        HantasSettings hantasSettings,
        MachineController machine)
    {
        _io = io;
        _inputAreas = inputAreas;
        _outputAreas = outputAreas;
        _adcBus = adcBus;
        _hantasSettings = hantasSettings;
        _machine = machine;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownCompleted)
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
            if (window is not AdcProtocolWindow and not OutputWindow)
            {
                window.Close();
            }
        }

        try
        {
            await CommandShutdown.WaitAsync(
                _adcProtocolWindow?.StopAsync() ?? Task.CompletedTask,
                _outputWindow?.ShutdownAsync() ?? Task.CompletedTask,
                ((MainViewModel)DataContext).ShutdownAsync(),
                _machine.ShutdownAsync());
            _shutdownCompleted = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            _closing = false;
            MessageBox.Show(this, exception.Message, "Shutdown Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenInputs(object sender, RoutedEventArgs e)
    {
        if (_inputWindow is not null)
        {
            _inputWindow.Activate();
            return;
        }

        _inputWindow = new InputWindow(_io, _inputAreas)
        {
            Owner = this,
        };
        _inputWindow.Closed += (_, _) => _inputWindow = null;
        _inputWindow.Show();
    }

    private void OnOpenOutputs(object sender, RoutedEventArgs e)
    {
        if (_outputWindow is not null)
        {
            _outputWindow.Activate();
            return;
        }

        _outputWindow = new OutputWindow(_io, _outputAreas)
        {
            Owner = this,
        };
        _outputWindow.Closed += (_, _) => _outputWindow = null;
        _outputWindow.Show();
    }

    private void OnOpenAdcProtocol(object sender, RoutedEventArgs e)
    {
        if (_adcProtocolWindow is not null)
        {
            _adcProtocolWindow.Activate();
            return;
        }

        _adcProtocolWindow = new AdcProtocolWindow(
            _adcBus,
            _hantasSettings,
            _machine)
        {
            Owner = this,
        };
        _adcProtocolWindow.Closed += (_, _) => _adcProtocolWindow = null;
        _adcProtocolWindow.Show();
    }

    private void RecipeFile_Selected(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string recipeName
            && DataContext is MainViewModel viewModel)
        {
            viewModel.RecipeEditor.LoadCommand.Execute(recipeName);
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_closing || sender is not MainViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ManualControlsEnabled))
        {
            _adcProtocolWindow?.RefreshControls();
        }

        if (e.PropertyName == nameof(MainViewModel.ManualOutputsEnabled)
            && !viewModel.ManualOutputsEnabled)
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
