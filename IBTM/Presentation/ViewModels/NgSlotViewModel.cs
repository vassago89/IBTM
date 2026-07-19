using CommunityToolkit.Mvvm.ComponentModel;

namespace IBTM.Presentation.ViewModels;

/// <summary>NG 스택 슬롯 (시각화용)</summary>
public partial class NgSlotViewModel : ObservableObject
{
    [ObservableProperty] private bool _isFilled;
}
