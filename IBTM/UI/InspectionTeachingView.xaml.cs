using System.Windows.Controls;
using System.Windows.Data;

namespace IBTM.UI;

public partial class InspectionTeachingView : UserControl
{
    public InspectionTeachingView()
    {
        InitializeComponent();
    }

    private void OnInspectionSettingChanged(object sender, DataTransferEventArgs e)
    {
        if (DataContext is InspectionTeachingViewModel viewModel)
            viewModel.OnInspectionSettingChanged(refreshBinaryImage: false);
    }

    private void OnDataMatrixSettingChanged(object sender, DataTransferEventArgs e)
    {
        if (DataContext is InspectionTeachingViewModel viewModel)
            viewModel.OnInspectionSettingChanged(refreshBinaryImage: true);
    }
}
