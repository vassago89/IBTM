using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IBTM.UI;

public partial class SupplyTeachingView : UserControl
{
    public SupplyTeachingView()
    {
        InitializeComponent();
    }

    private SupplyTeachingViewModel ViewModel =>
        (SupplyTeachingViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        ViewModel.Activate();

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        ViewModel.Deactivate();

    private void JogXPlus_Down(object sender, MouseButtonEventArgs e) =>
        ViewModel.JogXPlusCommand.Execute(null);

    private void JogXMinus_Down(object sender, MouseButtonEventArgs e) =>
        ViewModel.JogXMinusCommand.Execute(null);

    private void JogYPlus_Down(object sender, MouseButtonEventArgs e) =>
        ViewModel.JogYPlusCommand.Execute(null);

    private void JogYMinus_Down(object sender, MouseButtonEventArgs e) =>
        ViewModel.JogYMinusCommand.Execute(null);

    private void JogZPlus_Down(object sender, MouseButtonEventArgs e) =>
        ViewModel.JogZPlusCommand.Execute(null);

    private void JogZMinus_Down(object sender, MouseButtonEventArgs e) =>
        ViewModel.JogZMinusCommand.Execute(null);

    private void Jog_Up(object sender, MouseButtonEventArgs e) =>
        ViewModel.JogStopCommand.Execute(null);

    private void Jog_Cancel(object sender, MouseEventArgs e) =>
        ViewModel.JogStopCommand.Execute(null);

    private void RecipeFile_Selected(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string fileName)
        {
            ViewModel.LoadRecipeCommand.Execute(fileName);
        }
    }
}
