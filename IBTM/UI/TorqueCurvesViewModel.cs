using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.BoltFastening;

namespace IBTM.UI;

public partial class TorqueCurvesViewModel : ObservableObject
{
    public TorqueCurvesViewModel(long? jobId, IReadOnlyList<FasteningTorqueCurve> curves)
    {
        JobId = jobId;
        Curves = curves;
        SelectedCurve = curves.FirstOrDefault();
    }

    public long? JobId { get; }
    public IReadOnlyList<FasteningTorqueCurve> Curves { get; }

    [ObservableProperty]
    public partial FasteningTorqueCurve? SelectedCurve { get; set; }
}
