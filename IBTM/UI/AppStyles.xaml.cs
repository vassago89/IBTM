using System.Windows;
using System.Windows.Controls;

namespace IBTM.UI;

public partial class AppStyles : ResourceDictionary
{
    public AppStyles()
    {
        InitializeComponent();
    }

    private void OnComboBoxDropDownClick(object sender, RoutedEventArgs e)
    {
        var comboBox = (ComboBox)((Button)sender).TemplatedParent;
        comboBox.SetCurrentValue(ComboBox.IsDropDownOpenProperty, !comboBox.IsDropDownOpen);
    }
}
