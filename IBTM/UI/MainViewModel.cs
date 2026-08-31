using System;
using System.ComponentModel;
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
    private readonly bool _supplyTeachingEnabled;
    private readonly bool _stationTeachingEnabled;

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
        SettingsViewModel settingsViewModel,
        ManualHardwareViewModel manualHardwareViewModel,
        RecipeEditor recipeEditor,
        MachineState state,
        UnitSettings units,
        DriverSettings drivers)
    {
        _operationViewModel = operationViewModel;
        _supplyTeachingViewModel = supplyTeachingViewModel;
        _stationTeachingViewModel = stationTeachingViewModel;
        _boltTrainingViewModel = boltTrainingViewModel;
        _settingsViewModel = settingsViewModel;
        _manualHardwareViewModel = manualHardwareViewModel;
        RecipeEditor = recipeEditor;
        _state = state;
        _supplyTeachingEnabled = units.PcbSupply || units.PcbPlacement;
        _stationTeachingEnabled = units.PcbPlacement
                                  || units.BoltFastening
                                  || units.Inspection
                                  || units.NgConveyor;
        var virtualDrivers =
            Convert.ToInt32(drivers.Control == ControlDriver.Virtual)
            + Convert.ToInt32(drivers.Camera == CameraDriver.Virtual)
            + Convert.ToInt32(drivers.Bolt == BoltDriver.Virtual);
        Environment = virtualDrivers switch
        {
            0 => MachineEnvironmentDisplay.Physical,
            3 => MachineEnvironmentDisplay.Virtual,
            _ => MachineEnvironmentDisplay.Mixed,
        };
        state.Changed += OnMachineStateChanged;
        ActivateCurrentPage();
    }

    public OperationViewModel Operation => _operationViewModel;
    public RecipeEditor RecipeEditor { get; }
    public MachineEnvironmentDisplay Environment { get; }
    public bool RecipeToolsVisible =>
        SelectedPage is AppPage.SupplyTeaching
            or AppPage.StationTeaching;
    public bool RecipeEditingEnabled => !_state.IsRunning;
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
    public bool ManualControlsEnabled => _state.ManualControlsEnabled;
    public bool CurrentPageEnabled =>
        SelectedPage is AppPage.Operation
            or AppPage.Settings
            or AppPage.ManualHardware
            or AppPage.BoltTraining
        || _state.CanOperate;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(AppPage page)
    {
        DeactivateCurrentPage();
        SelectedPage = page;
        ActivateCurrentPage();
    }

    private bool CanNavigate(AppPage page) =>
        page == AppPage.Operation
        || ((page == AppPage.Settings && !_state.IsRunning)
            || (page == AppPage.ManualHardware && !_state.IsRunning)
            || (page == AppPage.BoltTraining
                && _state.ManualControlsEnabled)
            || (page == AppPage.SupplyTeaching
                && _supplyTeachingEnabled
                && _state.ManualControlsEnabled)
            || (page == AppPage.StationTeaching
                && _stationTeachingEnabled
                && _state.ManualControlsEnabled));

    private void ActivateCurrentPage()
    {
        switch (CurrentPage)
        {
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
        }
    }

    private void DeactivateCurrentPage()
    {
        switch (CurrentPage)
        {
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

    private void OnMachineStateChanged() =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(ManualControlsEnabled));
            OnPropertyChanged(nameof(CurrentPageEnabled));
            OnPropertyChanged(nameof(RecipeEditingEnabled));
            NavigateCommand.NotifyCanExecuteChanged();
            if (_state.AutomaticRunning
                && SelectedPage != AppPage.Operation)
            {
                Navigate(AppPage.Operation);
            }
            else if (!_state.CanOperate
                && CurrentPage is SupplyTeachingViewModel
                    or StationTeachingViewModel)
            {
                Navigate(AppPage.Operation);
            }
            else if (!_state.ManualControlsEnabled
                      && CurrentPage is BoltTrainingViewModel
                          { IsBusy: false })
            {
                Navigate(AppPage.Operation);
            }
        });

}
