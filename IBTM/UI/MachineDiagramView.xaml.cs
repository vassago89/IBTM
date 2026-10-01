using System.Windows;
using System.Windows.Controls;

namespace IBTM.UI;

public partial class MachineDiagramView : UserControl
{
    public static readonly DependencyProperty ShowStartCheckProperty;

    static MachineDiagramView()
    {
        ShowStartCheckProperty = DependencyProperty.Register(
            nameof(ShowStartCheck), typeof(bool), typeof(MachineDiagramView), new PropertyMetadata(false));
    }

    public MachineDiagramView()
    {
        InitializeComponent();
    }

    public bool ShowStartCheck
    {
        get => (bool)GetValue(ShowStartCheckProperty);
        set => SetValue(ShowStartCheckProperty, value);
    }
}
