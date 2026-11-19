using System.Windows;
using System.Windows.Controls;

namespace IBTM.UI;

public partial class MachineDiagramView : UserControl
{
    public static readonly DependencyProperty ShowStartCheckProperty;
    public static readonly DependencyProperty SelectedReviewBoltProperty;

    static MachineDiagramView()
    {
        ShowStartCheckProperty = DependencyProperty.Register(
            nameof(ShowStartCheck), typeof(bool), typeof(MachineDiagramView), new PropertyMetadata(false));
        SelectedReviewBoltProperty = DependencyProperty.Register(
            nameof(SelectedReviewBolt), typeof(FasteningResumeRow), typeof(MachineDiagramView));
    }

    public MachineDiagramView()
    {
        InitializeComponent();
    }

    public bool ShowStartCheck
    {
        get => (bool)GetValue(ShowStartCheckProperty);
        set => SetValue(ShowStartCheckProperty, value);
    }

    public FasteningResumeRow? SelectedReviewBolt
    {
        get => (FasteningResumeRow?)GetValue(SelectedReviewBoltProperty);
        set => SetValue(SelectedReviewBoltProperty, value);
    }
}
