using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IBTM.UI;

public partial class TeachingView : UserControl
{
    public TeachingView()
    {
        InitializeComponent();
    }

    private TeachingViewModel ViewModel => (TeachingViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Activate();
    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Deactivate();

    private void JogXPlus_Down(object sender, MouseButtonEventArgs e) => ViewModel.JogXPlusCommand.Execute(null);
    private void JogXMinus_Down(object sender, MouseButtonEventArgs e) => ViewModel.JogXMinusCommand.Execute(null);
    private void JogYPlus_Down(object sender, MouseButtonEventArgs e) => ViewModel.JogYPlusCommand.Execute(null);
    private void JogYMinus_Down(object sender, MouseButtonEventArgs e) => ViewModel.JogYMinusCommand.Execute(null);
    private void JogZPlus_Down(object sender, MouseButtonEventArgs e) => ViewModel.JogZPlusCommand.Execute(null);
    private void JogZMinus_Down(object sender, MouseButtonEventArgs e) => ViewModel.JogZMinusCommand.Execute(null);
    private void Jog_Up(object sender, MouseButtonEventArgs e) => ViewModel.JogStopCommand.Execute(null);
    private void Jog_Cancel(object sender, MouseEventArgs e) => ViewModel.JogStopCommand.Execute(null);

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
