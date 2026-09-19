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

public sealed partial class IoList<TRow, TSignal> : ObservableObject
    where TSignal : struct, Enum
{
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial KeyValuePair<HardwareArea?, string> SelectedArea { get; set; }

    public IoList(TRow[] rows, Func<TRow, IoSignal<TSignal>> signal)
    {
        Areas = [
            new(null, "All Units"),
            ..rows.Select(row => signal(row).Area)
                .Distinct()
                .Order()
                .Select(area => new KeyValuePair<HardwareArea?, string>(area, area.GetDescription())),
        ];
        FilteredRows = new ListCollectionView(rows
            .OrderBy(row => signal(row).Area)
            .ThenBy(row => signal(row).Section)
            .ThenBy(row => signal(row).Signal)
            .ToArray());
        FilteredRows.GroupDescriptions.Add(new AreaGroupDescription(signal));
        FilteredRows.GroupDescriptions.Add(new SectionGroupDescription(signal));
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

    private sealed class AreaGroupDescription : GroupDescription
    {
        private readonly Func<TRow, IoSignal<TSignal>> _signal;

        public AreaGroupDescription(Func<TRow, IoSignal<TSignal>> signal)
        {
            _signal = signal;
        }

        public override object GroupNameFromItem(object item, int level, CultureInfo culture)
        {
            return _signal((TRow)item).Area;
        }
    }

    private sealed class SectionGroupDescription : GroupDescription
    {
        private readonly Func<TRow, IoSignal<TSignal>> _signal;

        public SectionGroupDescription(Func<TRow, IoSignal<TSignal>> signal)
        {
            _signal = signal;
        }

        public override object? GroupNameFromItem(object item, int level, CultureInfo culture)
        {
            return _signal((TRow)item).Section;
        }
    }
}
