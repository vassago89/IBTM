using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class OutputWindow : Window
{
    private readonly IIoService _io;

    public OutputWindow(IIoService io)
    {
        _io = io;
        Rows = Enum.GetValues<OutputIo>()
            .Select(output => new OutputControlRow(
                output,
                io.Hardware.OutputFeedbacks.GetValueOrDefault(output)))
            .ToArray();

        InitializeComponent();
        DataContext = this;
        _io.InputChanged += OnInputChanged;
        Activated += (_, _) => Refresh();
    }

    public OutputControlRow[] Rows { get; }

    protected override void OnClosed(EventArgs e)
    {
        _io.InputChanged -= OnInputChanged;
        base.OnClosed(e);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        Refresh();
        StatusText.Text = "Output state refreshed";
    }

    private async void OnToggleOutput(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var row = (OutputControlRow)button.DataContext;
        var value = !_io.GetOutput(row.Output);
        button.IsEnabled = false;

        try
        {
            _io.SetOutput(row.Output, value);
            row.Refresh(_io);
            OutputList.Items.Refresh();

            if (row.HasFeedback)
            {
                StatusText.Text = $"Waiting for {row.Feedback}";
                await _io.WaitForOutputFeedbackAsync(row.Output, value);
            }

            StatusText.Text =
                $"{row.Output.GetDescription()} {(value ? "ON" : "OFF")}";
        }
        catch (IoFeedbackTimeoutException exception)
        {
            StatusText.Text = $"Alarm: {exception.Message}";
        }
        finally
        {
            row.Refresh(_io);
            OutputList.Items.Refresh();
            button.IsEnabled = true;
        }
    }

    private void Refresh()
    {
        foreach (var row in Rows)
        {
            row.Refresh(_io);
        }

        OutputList.Items.Refresh();
    }

    private void OnInputChanged(InputIo input, bool value) =>
        Dispatcher.BeginInvoke((Action)Refresh);
}

public sealed class OutputControlRow(
    OutputIo output,
    OutputFeedback? feedback)
{
    public OutputIo Output { get; } = output;
    public bool HasFeedback => feedback is not null;
    public bool OutputOn { get; private set; }
    public bool InputOn { get; private set; }
    public string Feedback { get; private set; } = "-";

    public void Refresh(IIoService io)
    {
        OutputOn = io.GetOutput(Output);
        if (feedback is null)
        {
            Feedback = "-";
            InputOn = false;
            return;
        }

        var expected = feedback.GetExpected(OutputOn);
        Feedback =
            $"{expected.Input.GetDescription()} = {(expected.Value ? "ON" : "OFF")}";
        InputOn = io.GetInput(expected.Input);
    }
}
