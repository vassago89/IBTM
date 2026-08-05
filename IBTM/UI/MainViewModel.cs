using System;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;

namespace IBTM.UI;

public enum AppPage
{
    [Description("Process Monitor")]
    Process,

    [Description("PCB Supply Teaching")]
    SupplyTeaching,

    [Description("Station Teaching")]
    StationTeaching,

    [Description("Settings")]
    Settings,
}

public partial class MainViewModel : ObservableObject
{
    private readonly ProcessViewModel _processViewModel;
    private readonly SupplyTeachingViewModel _supplyTeachingViewModel;
    private readonly StationTeachingViewModel _stationTeachingViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly EquipmentState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageEnabled))]
    private ObservableObject _currentPage;

    [ObservableProperty] private AppPage _selectedPage = AppPage.Process;

    public MainViewModel(
        ProcessViewModel processViewModel,
        SupplyTeachingViewModel supplyTeachingViewModel,
        StationTeachingViewModel stationTeachingViewModel,
        SettingsViewModel settingsViewModel,
        EquipmentState state,
        MachineSettings settings)
    {
        _processViewModel = processViewModel;
        _supplyTeachingViewModel = supplyTeachingViewModel;
        _stationTeachingViewModel = stationTeachingViewModel;
        _settingsViewModel = settingsViewModel;
        _state = state;
        HardwareName = settings.ControlDriver.GetDescription();
        _currentPage = processViewModel;
        state.Changed += OnEquipmentStateChanged;
        ActivateCurrentPage();
    }

    public string HardwareName { get; }
    public bool ManualControlsEnabled =>
        _state.ManualControlsEnabled;
    public bool CurrentPageEnabled =>
        CurrentPage is ProcessViewModel or SettingsViewModel
        || _state.CanOperate;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(AppPage page)
    {
        DeactivateCurrentPage();
        SelectedPage = page;
        CurrentPage = page switch
        {
            AppPage.Process => _processViewModel,
            AppPage.SupplyTeaching => _supplyTeachingViewModel,
            AppPage.StationTeaching => _stationTeachingViewModel,
            AppPage.Settings => _settingsViewModel,
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
        ActivateCurrentPage();
    }

    private bool CanNavigate(AppPage page) =>
        page == AppPage.Process
        || page == AppPage.Settings && !_state.EquipmentRunning
        || _state.ManualControlsEnabled;

    private void ActivateCurrentPage()
    {
        switch (CurrentPage)
        {
            case ProcessViewModel process:
                process.RefreshEquipmentState();
                break;
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

    private void OnEquipmentStateChanged() =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(ManualControlsEnabled));
            OnPropertyChanged(nameof(CurrentPageEnabled));
            NavigateCommand.NotifyCanExecuteChanged();
            if (!_state.CanOperate
                && CurrentPage is SupplyTeachingViewModel
                    or StationTeachingViewModel)
            {
                Navigate(AppPage.Process);
            }
        });
}
