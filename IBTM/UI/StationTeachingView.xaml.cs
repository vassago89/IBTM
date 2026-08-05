using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IBTM.UI;

public partial class StationTeachingView : UserControl
{
    public StationTeachingView()
    {
        InitializeComponent();
    }

    private StationTeachingViewModel ViewModel => (StationTeachingViewModel)DataContext;

    private void CameraImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image { Source: ImageSource source } image
            || image.ActualWidth <= 0
            || image.ActualHeight <= 0)
        {
            return;
        }

        var pos = e.GetPosition(image);
        var scale = Math.Min(
            image.ActualWidth / source.Width,
            image.ActualHeight / source.Height);
        var renderedWidth = source.Width * scale;
        var renderedHeight = source.Height * scale;
        var left = (image.ActualWidth - renderedWidth) / 2.0;
        var top = (image.ActualHeight - renderedHeight) / 2.0;

        if (pos.X < left
            || pos.X > left + renderedWidth
            || pos.Y < top
            || pos.Y > top + renderedHeight)
        {
            return;
        }

        var imgPos = new Point(
            (pos.X - left) / scale,
            (pos.Y - top) / scale);

        ViewModel.CameraClickCommand.Execute(imgPos);
        e.Handled = true;
    }

    private void RecipeFile_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string fileName)
        {
            ViewModel.LoadRecipeCommand.Execute(fileName);
        }
    }
}
