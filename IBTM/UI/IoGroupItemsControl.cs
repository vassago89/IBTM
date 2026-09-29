using System.Collections;
using System.Windows.Controls;

namespace IBTM.UI;

public sealed class IoGroupItemsControl : ItemsControl
{
    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        if (Parent is ScrollViewer scroll)
            scroll.ScrollToHome();
    }
}
