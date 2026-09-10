using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class IoList<TRow, TSignal> : ObservableObject
    where TSignal : struct, Enum
{
    [ObservableProperty]
    private string _searchText = string.Empty;
    [ObservableProperty]
    private KeyValuePair<HardwareArea?, string> _selectedArea;

    public IoList(TRow[] rows, Func<TRow, IoSignal<TSignal>> signal, string path = "")
    {
        Areas = [
            new(null, "All Units"),
            ..rows.Select(row => signal(row).Area)
                .Distinct()
                .Order()
                .Select(area => new KeyValuePair<HardwareArea?, string>(area, area.GetDescription())),
        ];
        _selectedArea = Areas[0];
        FilteredRows = new ListCollectionView(rows);
        var prefix = path.Length == 0 ? "" : path + ".";
        foreach (var property in new[]
        {
            nameof(IoSignal<TSignal>.Area),
            nameof(IoSignal<TSignal>.Section)
        })
        {
            FilteredRows.SortDescriptions.Add(new(prefix + property, ListSortDirection.Ascending));
            FilteredRows.GroupDescriptions.Add(new PropertyGroupDescription(prefix + property));
        }

        FilteredRows.SortDescriptions.Add(
            new(prefix + nameof(IoSignal<TSignal>.Signal), ListSortDirection.Ascending));
        FilteredRows.Filter = item =>
        {
            var row = signal((TRow)item);
            return (SelectedArea.Key is null || row.Area == SelectedArea.Key)
                && (Matches(row)
                    || row is IoOutputStatus output
                    && output.Feedback.Any(Matches));
        };
    }

    public ICollectionView FilteredRows { get; }
    public KeyValuePair<HardwareArea?, string>[] Areas { get; }

    private bool Matches<T>(IoSignal<T> row)
        where T : struct, Enum
    {
        return row.Signal.GetDescription().Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Signal.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Address.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredRows.Refresh();
    }

    partial void OnSelectedAreaChanged(KeyValuePair<HardwareArea?, string> value)
    {
        FilteredRows.Refresh();
    }
}
