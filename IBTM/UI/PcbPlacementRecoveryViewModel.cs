using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;

namespace IBTM.UI;

public sealed class PcbPlacementRecoveryItem
{
    public required HeatSinkSlot HeatSink { get; init; }
    public bool Completed { get; set; }
}

public sealed partial class PcbPlacementRecoveryViewModel(
    IReadOnlyList<PcbPlacementRecoveryItem> items,
    bool repeat = false) : ObservableObject
{
    public string Instructions { get; } = repeat
        ? "Repeat: check only completed PCB round trips. Unchecked heat sinks reuse their existing PCB."
        : "Check heat sinks with PCB already placed. Unchecked heat sinks run again.";

    [ObservableProperty]
    private bool? _dialogResult;

    [RelayCommand]
    private void Apply()
    {
        DialogResult = true;
    }

    public IReadOnlyList<PcbPlacementRecoveryItem> Items { get; } = items;
}
