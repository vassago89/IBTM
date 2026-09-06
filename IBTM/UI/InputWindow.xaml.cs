using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public partial class InputWindow : Window, INotifyPropertyChanged
{
    private readonly IIoService _io;
    private readonly VirtualIoService? _virtualIo;
    private string _searchText = string.Empty;
    private KeyValuePair<HardwareArea?, string> _selectedArea;

    public InputWindow(
        IIoService io,
        IReadOnlyDictionary<InputIo, HardwareArea> inputAreas)
    {
        _io = io;
        _virtualIo = io as VirtualIoService;
        Rows = Enum.GetValues<InputIo>()
            .Select(input => new InputControlRow(input, inputAreas[input]))
            .ToArray();
        Areas =
        [
            new(null, "All Units"),
            .. inputAreas.Values.Distinct().Order().Select(area =>
                new KeyValuePair<HardwareArea?, string>(area, area.GetDescription())),
        ];
        _selectedArea = Areas[0];
        FilteredRows = CollectionViewSource.GetDefaultView(Rows);
        FilteredRows.SortDescriptions.Add(new(
            nameof(InputControlRow.Area),
            ListSortDirection.Ascending));
        FilteredRows.SortDescriptions.Add(new(
            nameof(InputControlRow.Input),
            ListSortDirection.Ascending));
        FilteredRows.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(InputControlRow.Area)));
        FilteredRows.Filter = item =>
        {
            var row = (InputControlRow)item;
            return (_selectedArea.Key is null || row.Area == _selectedArea.Key)
                && (row.Input.GetDescription().Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                    || row.Input.ToString().Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        };

        InitializeComponent();
        _io.InputChanged += OnInputChanged;
        try
        {
            foreach (var row in Rows)
            {
                row.Set(_io.GetInput(row.Input));
            }
        }
        catch
        {
            _io.InputChanged -= OnInputChanged;
            throw;
        }
        if (_virtualIo is not null)
        {
            _virtualIo.AutoResponseChanged += OnAutoResponseChanged;
        }
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public InputControlRow[] Rows { get; }
    public ICollectionView FilteredRows { get; }
    public KeyValuePair<HardwareArea?, string>[] Areas { get; }
    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            FilteredRows.Refresh();
            PropertyChanged?.Invoke(this, new(nameof(SearchText)));
        }
    }
    public KeyValuePair<HardwareArea?, string> SelectedArea
    {
        get => _selectedArea;
        set
        {
            _selectedArea = value;
            FilteredRows.Refresh();
            PropertyChanged?.Invoke(this, new(nameof(SelectedArea)));
        }
    }
    public bool IsVirtual => _virtualIo is not null;
    public bool AutoResponseEnabled
    {
        get => _virtualIo?.AutoResponseEnabled ?? false;
        set
        {
            if (_virtualIo is not null)
            {
                _virtualIo.AutoResponseEnabled = value;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _io.InputChanged -= OnInputChanged;
        if (_virtualIo is not null)
        {
            _virtualIo.AutoResponseChanged -= OnAutoResponseChanged;
        }
        base.OnClosed(e);
    }

    private void OnToggleInput(object sender, RoutedEventArgs e)
    {
        var row = (InputControlRow)((Button)sender).DataContext;
        _virtualIo!.SetInput(row.Input, !_virtualIo.GetInput(row.Input));
    }

    private void OnInputChanged(InputIo input, bool value) =>
        Dispatcher.BeginInvoke((Action)(() =>
            Rows[(int)input].Set(value)));

    private void OnAutoResponseChanged() =>
        Dispatcher.BeginInvoke((Action)(() =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AutoResponseEnabled)))));
}

public sealed class InputControlRow(
    InputIo input,
    HardwareArea area) : ObservableObject
{
    private bool _isOn;

    public InputIo Input { get; } = input;
    public HardwareArea Area { get; } = area;
    public bool IsOn => _isOn;

    public void Set(bool value)
    {
        _isOn = value;
        OnPropertyChanged(nameof(IsOn));
    }
}
