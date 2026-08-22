using System.Windows.Controls;

namespace IBTM.UI;

public partial class StationTeachingView : UserControl
{
    public StationTeachingView()
    {
        InitializeComponent();
    }

    private StationTeachingViewModel ViewModel => (StationTeachingViewModel)DataContext;

    private void RecipeFile_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string fileName)
        {
            ViewModel.RecipeEditor.LoadCommand.Execute(fileName);
        }
    }
}
