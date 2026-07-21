using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.Presentation.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ProcessViewModel _processViewModel;
    private readonly TeachingViewModel _teachingViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty] private ObservableObject _currentPage;

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
        CurrentPage = page switch
        {
            "Teaching" => _teachingViewModel,
            "Settings" => _settingsViewModel,
            _ => _processViewModel,
        };
    }
}
