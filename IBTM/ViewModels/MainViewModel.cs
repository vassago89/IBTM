using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ProcessViewModel _processViewModel;

    [ObservableProperty] private ObservableObject _currentPage;
    [ObservableProperty] private string _currentPageName = "Process";

    public MainViewModel(ProcessViewModel processViewModel)
    {
        _processViewModel = processViewModel;
        _currentPage = processViewModel;
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPageName = page;
        CurrentPage = page switch
        {
            "Process" => _processViewModel,
            _         => _processViewModel   // 추후 ManualViewModel, SettingsViewModel 추가
        };
    }
}
