using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class IoListViewModel<TRow, TSignal> : ObservableObject
    where TSignal : struct, Enum
{
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial KeyValuePair<HardwareArea?, string> SelectedArea { get; set; }

    public IoListViewModel(TRow[] rows, Func<TRow, IoSignal<TSignal>> signal)
    {
        Areas = [
            new(null, UiText.Get("All devices")),
            ..rows.Select(row => signal(row).Area)
                .Distinct()
                .Order()
                .Select(area => new KeyValuePair<HardwareArea?, string>(area, UiText.Get(area))),
        ];
        FilteredRows = new ListCollectionView(rows
            .OrderBy(row => signal(row).Area)
            .ThenBy(row => signal(row).Section)
            .ThenBy(row => signal(row).Signal)
            .ToArray());
        FilteredRows.GroupDescriptions.Add(new SignalGroupDescription(row => signal(row).Area));
        FilteredRows.GroupDescriptions.Add(new SignalGroupDescription(row => signal(row).Section));
        SelectedArea = Areas[0];
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
        var search = SearchText.Trim();
        return UiText.Get(row.Signal).Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Signal.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredRows.Refresh();
    }

    partial void OnSelectedAreaChanged(KeyValuePair<HardwareArea?, string> value)
    {
        FilteredRows.Refresh();
    }

    private sealed class SignalGroupDescription : GroupDescription
    {
        private readonly Func<TRow, object?> _groupKey;

        public SignalGroupDescription(Func<TRow, object?> groupKey)
        {
            _groupKey = groupKey;
        }

        public override object? GroupNameFromItem(object item, int level, CultureInfo culture)
        {
            return _groupKey((TRow)item);
        }
    }
}
