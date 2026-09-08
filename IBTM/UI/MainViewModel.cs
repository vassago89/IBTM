using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Inspection.Training;

namespace IBTM.UI;

public enum AppPage
{
    [Description("Operation")]
    Operation,

    [Description("Supply Teaching")]
    SupplyTeaching,

    [Description("Station Teaching")]
    StationTeaching,

    [Description("Bolt Training")]
    BoltTraining,

    [Description("Settings")]
    Settings,

    [Description("Manual Control")]
    ManualHardware,
}

public enum MachineEnvironmentDisplay
{
    [Description("Physical")]
    Physical,

    [Description("Mixed")]
    Mixed,

    [Description("Virtual")]
    Virtual,
}

public partial class MainViewModel : ObservableObject
{
    private readonly OperationViewModel _operationViewModel;
    private readonly SupplyTeachingViewModel _supplyTeachingViewModel;
    private readonly StationTeachingViewModel _stationTeachingViewModel;
    private readonly BoltTrainingViewModel _boltTrainingViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ManualHardwareViewModel _manualHardwareViewModel;
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly UnitSettings _units;
    private readonly bool _virtualBolt;
    private readonly IAsyncRelayCommand[] _recipeEditingCommands;
    private int _stateRefreshQueued;
    private bool _shuttingDown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    [NotifyPropertyChangedFor(nameof(CurrentPageEnabled))]
    [NotifyPropertyChangedFor(nameof(RecipeToolsVisible))]
    [NotifyPropertyChangedFor(nameof(OperationPageSelected))]
    private AppPage _selectedPage = AppPage.Operation;

    public MainViewModel(
        OperationViewModel operationViewModel,
        SupplyTeachingViewModel supplyTeachingViewModel,
        StationTeachingViewModel stationTeachingViewModel,
        BoltTrainingViewModel boltTrainingViewModel,
        BoltImageCollector imageCollector,
        SettingsViewModel settingsViewModel,
        ManualHardwareViewModel manualHardwareViewModel,
        RecipeEditor recipeEditor,
        MachineState state,
        UnitSettings units,
        DriverSettings drivers,
        MachineController machine)
    {
        _operationViewModel = operationViewModel;
        _supplyTeachingViewModel = supplyTeachingViewModel;
        _stationTeachingViewModel = stationTeachingViewModel;
        _boltTrainingViewModel = boltTrainingViewModel;
        ImageCollector = imageCollector;
        _settingsViewModel = settingsViewModel;
        _manualHardwareViewModel = manualHardwareViewModel;
        RecipeEditor = recipeEditor;
        _state = state;
        _machine = machine;
        _units = units;
        var controlVirtual = drivers.Control == ControlDriver.Virtual;
        var cameraVirtual = drivers.Camera == CameraDriver.Virtual;
        var boltVirtual = drivers.Bolt == BoltDriver.Virtual;
        var inspectionSimulated = units.Inspection
                                  && drivers.Inspection == InspectionAlgorithm.Virtual;
        _virtualBolt = boltVirtual;
        Environment = (controlVirtual, cameraVirtual, boltVirtual) switch
        {
            (true, true, true) => MachineEnvironmentDisplay.Virtual,
            (false, false, false) when !inspectionSimulated =>
                MachineEnvironmentDisplay.Physical,
            _ => MachineEnvironmentDisplay.Mixed,
        };
        _recipeEditingCommands =
        [
            recipeEditor.SaveCommand,
            recipeEditor.LoadCommand,
            supplyTeachingViewModel.TeachCurrentPositionCommand,
            supplyTeachingViewModel.MoveToPointCommand,
            supplyTeachingViewModel.SetOutputOnCommand,
            supplyTeachingViewModel.SetOutputOffCommand,
            stationTeachingViewModel.TeachCurrentPositionCommand,
            stationTeachingViewModel.TeachImagePointCommand,
            stationTeachingViewModel.MoveToPointCommand,
            stationTeachingViewModel.ReturnFromPickupCommand,
            stationTeachingViewModel.SetOutputOnCommand,
            stationTeachingViewModel.SetOutputOffCommand,
            stationTeachingViewModel.CaptureCarrierImagesCommand,
            stationTeachingViewModel.CaptureInspectionCommand,
            stationTeachingViewModel.ReinspectImageCommand,
            stationTeachingViewModel.CollectBoltImagesCommand,
            stationTeachingViewModel.TeachImageRegionCommand,
        ];
        foreach (var command in _recipeEditingCommands)
        {
            command.PropertyChanged += OnRecipeEditingChanged;
        }
        state.DisplayChanged += OnMachineStateChanged;
        ActivateCurrentPage();
    }

    public OperationViewModel Operation => _operationViewModel;
    public RecipeEditor RecipeEditor { get; }
    public BoltImageCollector ImageCollector { get; }
    public MachineEnvironmentDisplay Environment { get; }
    public bool RecipeToolsVisible =>
        SelectedPage is AppPage.SupplyTeaching
            or AppPage.StationTeaching;
    public bool RecipeEditingEnabled =>
        _state.ManualMode
        && !_state.IsRunning
        && Array.TrueForAll(_recipeEditingCommands, static command => !command.IsRunning);
    public bool OperationPageSelected =>
        SelectedPage == AppPage.Operation;
    public ObservableObject CurrentPage => SelectedPage switch
    {
        AppPage.Operation => _operationViewModel,
        AppPage.SupplyTeaching => _supplyTeachingViewModel,
        AppPage.StationTeaching => _stationTeachingViewModel,
        AppPage.BoltTraining => _boltTrainingViewModel,
        AppPage.Settings => _settingsViewModel,
        AppPage.ManualHardware => _manualHardwareViewModel,
        _ => throw new ArgumentOutOfRangeException(nameof(SelectedPage)),
    };
    public bool ManualControlsEnabled => _state.Display.ManualControlsEnabled;
    public bool ManualOutputsEnabled => _state.ManualOutputsEnabled;
    public bool AdcProtocolEnabled =>
        _virtualBolt || _machine.AdcProtocolAvailable;
    public bool CurrentPageEnabled =>
        SelectedPage is AppPage.Operation
            or AppPage.Settings
            or AppPage.ManualHardware
        || (!RecipeEditor.SaveCommand.IsRunning
            && !RecipeEditor.LoadCommand.IsRunning);

    public Task ShutdownAsync()
    {
        _shuttingDown = true;
        _state.DisplayChanged -= OnMachineStateChanged;
        foreach (var command in _recipeEditingCommands)
        {
            command.PropertyChanged -= OnRecipeEditingChanged;
        }
        return Task.WhenAll(
            _operationViewModel.ShutdownAsync(),
            _supplyTeachingViewModel.ShutdownAsync(),
            _stationTeachingViewModel.ShutdownAsync(),
            _manualHardwareViewModel.ShutdownAsync(),
            _boltTrainingViewModel.ShutdownAsync(),
            _settingsViewModel.ShutdownAsync(),
            RecipeEditor.ShutdownAsync());
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(AppPage page) => SelectedPage = page;

    partial void OnSelectedPageChanging(AppPage value) => DeactivateCurrentPage();

    partial void OnSelectedPageChanged(AppPage value)
    {
        ActivateCurrentPage();
        if (value == AppPage.Operation)
        {
            _state.Refresh();
        }
    }

    private bool CanNavigate(AppPage page) =>
        page == AppPage.Operation
        || !_state.IsRunning && page switch
        {
            AppPage.Settings or AppPage.ManualHardware => true,
            AppPage.BoltTraining => _state.ManualMode && _state.SafetyReady,
            AppPage.SupplyTeaching => _state.ManualMode && (_units.PcbSupply || _units.PcbPlacement),
            AppPage.StationTeaching => _state.ManualMode
                && (_units.PcbPlacement || _units.BoltFastening || _units.Inspection || _units.NgCarrierTransfer),
            _ => false,
        };

    private void ActivateCurrentPage()
    {
        switch (CurrentPage)
        {
            case OperationViewModel operation:
                operation.Activate();
                break;
            case SupplyTeachingViewModel supply:
                supply.Activate();
                break;
            case StationTeachingViewModel stationTeaching:
                stationTeaching.Activate();
                break;
            case ManualHardwareViewModel manualHardware:
                manualHardware.Activate();
                break;
            case BoltTrainingViewModel training:
                training.Activate();
                break;
            case SettingsViewModel settings:
                settings.RefreshCommands();
                break;
        }
    }

    private void DeactivateCurrentPage()
    {
        switch (CurrentPage)
        {
            case OperationViewModel operation:
                operation.Deactivate();
                break;
            case SupplyTeachingViewModel supply:
                supply.Deactivate();
                break;
            case StationTeachingViewModel stationTeaching:
                stationTeaching.Deactivate();
                break;
            case ManualHardwareViewModel manualHardware:
                manualHardware.Deactivate();
                break;
            case BoltTrainingViewModel training:
                training.Deactivate();
                break;
        }
    }

    private void OnRecipeEditingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
        {
            OnPropertyChanged(nameof(RecipeEditingEnabled));
            OnPropertyChanged(nameof(CurrentPageEnabled));
        }
    }

    private void OnMachineStateChanged()
    {
        if (_shuttingDown
            || Interlocked.Exchange(ref _stateRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _stateRefreshQueued, 0);
            if (_shuttingDown)
            {
                return;
            }

            OnPropertyChanged(nameof(ManualControlsEnabled));
            OnPropertyChanged(nameof(ManualOutputsEnabled));
            OnPropertyChanged(nameof(AdcProtocolEnabled));
            OnPropertyChanged(nameof(CurrentPageEnabled));
            OnPropertyChanged(nameof(RecipeEditingEnabled));
            NavigateCommand.NotifyCanExecuteChanged();
            if (_state.AutomaticRunning
                && SelectedPage != AppPage.Operation)
            {
                Navigate(AppPage.Operation);
            }
            else if (!_state.ManualMode
                && CurrentPage is SupplyTeachingViewModel
                    or StationTeachingViewModel)
            {
                Navigate(AppPage.Operation);
            }
            else if ((!_state.SafetyReady || !_state.ManualMode)
                      && CurrentPage is BoltTrainingViewModel
                      { IsBusy: false })
            {
                Navigate(AppPage.Operation);
            }

            if (CurrentPage is SettingsViewModel settings)
                settings.RefreshCommands();
        });
    }

}
