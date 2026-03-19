using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ProcessViewModel _processViewModel;
    private readonly TeachingViewModel _teachingViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty] private ObservableObject _currentPage;
    [ObservableProperty] private string _currentPageName = "Process";

    public MainViewModel(
        ProcessViewModel processViewModel,
        TeachingViewModel teachingViewModel,
        SettingsViewModel settingsViewModel)
    {
        _processViewModel = processViewModel;
        _teachingViewModel = teachingViewModel;
        _settingsViewModel = settingsViewModel;
        _currentPage = processViewModel;
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPageName = page;
        CurrentPage = page switch
        {
            "Process"  => _processViewModel,
            "Teaching" => _teachingViewModel,
            "Settings" => _settingsViewModel,
            _          => _processViewModel,
        };
    }
}
