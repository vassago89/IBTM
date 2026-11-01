using System;
using System.Windows;
using System.Windows.Controls;

namespace IBTM.UI;

public sealed class IoGroupPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 560 : availableSize.Width;
        var columns = InternalChildren.Count > 1 && width >= 560 ? 2 : 1;
        var columnWidth = width / columns;
        double left = 0;
        double right = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new(columnWidth, double.PositiveInfinity));
            if (columns == 1 || left <= right)
                left += child.DesiredSize.Height;
            else
                right += child.DesiredSize.Height;
        }
        return new(width, Math.Max(left, right));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = InternalChildren.Count > 1 && finalSize.Width >= 560 ? 2 : 1;
        var columnWidth = finalSize.Width / columns;
        double left = 0;
        double right = 0;
        foreach (UIElement child in InternalChildren)
        {
            var height = child.DesiredSize.Height;
            if (columns == 1 || left <= right)
            {
                child.Arrange(new(0, left, columnWidth, height));
                left += height;
            }
            else
            {
                child.Arrange(new(columnWidth, right, columnWidth, height));
                right += height;
            }
        }
        return finalSize;
    }
}
