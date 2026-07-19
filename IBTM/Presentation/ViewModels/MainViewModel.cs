using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.Presentation.ViewModels;

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
        var (pageName, viewModel) = page switch
        {
            "Teaching" => ("Teaching", (ObservableObject)_teachingViewModel),
            "Settings" => ("Settings", _settingsViewModel),
            _ => ("Process", _processViewModel),
        };
        CurrentPageName = pageName;
        CurrentPage = viewModel;
    }
}
