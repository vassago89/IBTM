using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Core;

namespace IBTM.UI;

public sealed class BoltFasteningRecoveryItem
{
    public required HeatSinkSlot HeatSink { get; init; }
    public required int Number { get; init; }
    public required FasteningPass Pass { get; init; }
    public bool Completed { get; set; }
}

public sealed partial class BoltFasteningRecoveryViewModel(
    IReadOnlyList<BoltFasteningRecoveryItem> items,
    bool heatSink1Present,
    bool heatSink2Present) : ObservableObject
{
    [ObservableProperty]
    private bool? _dialogResult;

    [RelayCommand]
    private void Apply()
    {
        DialogResult = true;
    }

    public IReadOnlyList<BoltFasteningRecoveryItem> Items { get; } = items;
    public bool HeatSink1Present { get; } = heatSink1Present;
    public bool HeatSink2Present { get; } = heatSink2Present;
}
