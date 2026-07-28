using System.Windows;
using System.Windows.Controls;

namespace IBTM.UI;

public partial class ProcessView : UserControl
{
    public ProcessView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        ((ProcessViewModel)DataContext).RefreshEquipmentState();
}
