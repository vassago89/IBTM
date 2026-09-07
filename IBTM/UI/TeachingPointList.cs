using System.Windows.Controls;

namespace IBTM.UI;

public sealed class TeachingPointList : ListBox
{
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (SelectedItem is not null) ScrollIntoView(SelectedItem);
    }
}
