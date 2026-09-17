using System.Windows.Controls;

namespace IBTM.UI;

public sealed class ComboBoxDropDownButton : Button
{
    protected override void OnClick()
    {
        base.OnClick();
        var comboBox = (ComboBox)TemplatedParent;
        comboBox.SetCurrentValue(ComboBox.IsDropDownOpenProperty, !comboBox.IsDropDownOpen);
    }
}
