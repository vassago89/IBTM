using System;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public enum AppPage
{
    [Description("Operation")]
    Operation,

    [Description("PCB Supply Teaching")]
    SupplyTeaching,

    [Description("Station Teaching")]
    StationTeaching,

    [Description("Settings")]
    Settings,
}

public partial class MainViewModel : ObservableObject
{
    private readonly OperationViewModel _operationViewModel;
    private readonly SupplyTeachingViewModel _supplyTeachingViewModel;
    private readonly StationTeachingViewModel _stationTeachingViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly MachineState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    [NotifyPropertyChangedFor(nameof(CurrentPageEnabled))]
    private AppPage _selectedPage = AppPage.Operation;

    public MainViewModel(
        OperationViewModel operationViewModel,
        SupplyTeachingViewModel supplyTeachingViewModel,
        StationTeachingViewModel stationTeachingViewModel,
        SettingsViewModel settingsViewModel,
        MachineState state,
        DriverSettings drivers)
    {
        _operationViewModel = operationViewModel;
        _supplyTeachingViewModel = supplyTeachingViewModel;
        _stationTeachingViewModel = stationTeachingViewModel;
        _settingsViewModel = settingsViewModel;
        _state = state;
        Driver = drivers.Control;
        state.Changed += OnMachineStateChanged;
        ActivateCurrentPage();
    }

    public ControlDriver Driver { get; }
    public ObservableObject CurrentPage => SelectedPage switch
    {
        AppPage.Operation => _operationViewModel,
        AppPage.SupplyTeaching => _supplyTeachingViewModel,
        AppPage.StationTeaching => _stationTeachingViewModel,
        AppPage.Settings => _settingsViewModel,
        _ => throw new ArgumentOutOfRangeException(nameof(SelectedPage)),
    };
    public bool ManualControlsEnabled => _state.ManualControlsEnabled;
    public bool CurrentPageEnabled =>
        SelectedPage is AppPage.Operation or AppPage.Settings
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
            || (page is AppPage.SupplyTeaching or AppPage.StationTeaching
                && _state.CanOperate
                && !_state.IsRunning));

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
            case SettingsViewModel settings:
                settings.Activate();
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
            case SettingsViewModel settings:
                settings.Deactivate();
                break;
        }
    }

    private void OnMachineStateChanged() =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(ManualControlsEnabled));
            OnPropertyChanged(nameof(CurrentPageEnabled));
            NavigateCommand.NotifyCanExecuteChanged();
            if (!_state.CanOperate
                && CurrentPage is SupplyTeachingViewModel
                    or StationTeachingViewModel)
            {
                Navigate(AppPage.Operation);
            }
        });

}
