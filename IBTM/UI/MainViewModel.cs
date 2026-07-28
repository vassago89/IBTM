using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.UI;

public enum AppPage
{
    Process,
    SupplyTeaching,
    Teaching,
    Settings,
}

public partial class MainViewModel : ObservableObject
{
    private readonly ProcessViewModel _processViewModel;
    private readonly SupplyTeachingViewModel _supplyTeachingViewModel;
    private readonly TeachingViewModel _teachingViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty] private ObservableObject _currentPage;

    public MainViewModel(
        ProcessViewModel processViewModel,
        SupplyTeachingViewModel supplyTeachingViewModel,
        TeachingViewModel teachingViewModel,
        SettingsViewModel settingsViewModel,
        MachineSettings settings)
    {
        _processViewModel = processViewModel;
        _supplyTeachingViewModel = supplyTeachingViewModel;
        _teachingViewModel = teachingViewModel;
        _settingsViewModel = settingsViewModel;
        HardwareName = settings.Driver.ToString().ToUpperInvariant();
        _currentPage = processViewModel;
        _processViewModel.PropertyChanged += OnProcessPropertyChanged;
    }

    public string HardwareName { get; }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(AppPage page)
    {
        CurrentPage = page switch
        {
            AppPage.Process => _processViewModel,
            AppPage.SupplyTeaching => _supplyTeachingViewModel,
            AppPage.Teaching => _teachingViewModel,
            AppPage.Settings => _settingsViewModel,
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
    }

    private bool CanNavigate(AppPage page) =>
        page == AppPage.Process || _processViewModel.ManualControlsEnabled;

    private void OnProcessPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessViewModel.ManualControlsEnabled))
        {
            NavigateCommand.NotifyCanExecuteChanged();
        }
    }
}
