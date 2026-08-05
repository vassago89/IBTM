using System.Windows.Controls;

namespace IBTM.UI;

public partial class SupplyTeachingView : UserControl
{
    public SupplyTeachingView()
    {
        InitializeComponent();
    }

    private SupplyTeachingViewModel ViewModel =>
        (SupplyTeachingViewModel)DataContext;

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
