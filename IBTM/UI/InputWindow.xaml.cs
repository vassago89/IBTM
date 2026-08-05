using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public partial class InputWindow : Window
{
    private readonly IIoService _io;
    private readonly VirtualIoService? _virtualIo;

    public InputWindow(IIoService io)
    {
        _io = io;
        _virtualIo = io as VirtualIoService;
        Rows = Enum.GetValues<InputIo>()
            .Select(input => new InputControlRow(input))
            .ToArray();

        InitializeComponent();
        DataContext = this;
        _io.InputChanged += OnInputChanged;
        Activated += (_, _) => Refresh();
        Refresh();
    }

    public InputControlRow[] Rows { get; }
    public bool IsVirtual => _virtualIo is not null;

    protected override void OnClosed(EventArgs e)
    {
        _io.InputChanged -= OnInputChanged;
        base.OnClosed(e);
    }

    private void OnToggleInput(object sender, RoutedEventArgs e)
    {
        var row = (InputControlRow)((Button)sender).DataContext;
        _virtualIo!.SetInput(row.Input, !row.IsOn);
    }

    private void OnInputChanged(InputIo input, bool value) =>
        Dispatcher.BeginInvoke((Action)(() =>
        {
            Rows.Single(row => row.Input == input).IsOn = value;
            InputList.Items.Refresh();
        }));

    private void Refresh()
    {
        foreach (var row in Rows)
        {
            row.IsOn = _io.GetInput(row.Input);
        }

        InputList.Items.Refresh();
    }
}

public sealed class InputControlRow(InputIo input)
{
    public InputIo Input { get; } = input;
    public bool IsOn { get; set; }
}
