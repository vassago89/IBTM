using System.ComponentModel;
using System.Windows;
using IBTM.Device;

namespace IBTM.UI;

public partial class MainWindow : Window
{
    private readonly IIoService _io;
    private InputWindow? _inputWindow;
    private OutputWindow? _outputWindow;
    private AdcProtocolWindow? _adcProtocolWindow;

    public MainWindow(MainViewModel viewModel, IIoService io)
    {
        _io = io;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnOpenInputs(object sender, RoutedEventArgs e)
    {
        if (_inputWindow is not null)
        {
            _inputWindow.Activate();
            return;
        }

        _inputWindow = new InputWindow(_io)
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

        _outputWindow = new OutputWindow(_io)
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

        _adcProtocolWindow = new AdcProtocolWindow
        {
            Owner = this,
        };
        _adcProtocolWindow.Closed += (_, _) => _adcProtocolWindow = null;
        _adcProtocolWindow.Show();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ManualControlsEnabled)
            && sender is MainViewModel { ManualControlsEnabled: false })
        {
            _outputWindow?.Close();
        }
    }
}
