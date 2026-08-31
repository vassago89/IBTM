using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
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
                io,
                output,
                io.GetOutputFeedback(output)))
            .ToArray();

        InitializeComponent();
        DataContext = this;
        _io.InputChanged += OnInputChanged;
        _io.OutputChanged += OnOutputChanged;
    }

    public OutputControlRow[] Rows { get; }

    protected override void OnClosed(EventArgs e)
    {
        _io.InputChanged -= OnInputChanged;
        _io.OutputChanged -= OnOutputChanged;
        base.OnClosed(e);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows)
        {
            row.ClearTimeout();
        }

        Refresh();
    }

    private async void OnToggleOutput(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var row = (OutputControlRow)button.DataContext;
        var value = !_io.GetOutput(row.Output);
        button.IsEnabled = false;
        row.ClearTimeout();

        try
        {
            _io.SetOutput(row.Output, value);
            Refresh();

            if (row.HasFeedback)
            {
                await _io.WaitForOutputFeedbackAsync(row.Output, value);
            }
        }
        catch (IoTimeoutException)
        {
            row.MarkTimeout();
        }
        finally
        {
            Refresh();
            button.IsEnabled = true;
        }
    }

    private void Refresh()
    {
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }

    private void OnInputChanged(InputIo input, bool value) =>
        Dispatcher.BeginInvoke((Action)Refresh);

    private void OnOutputChanged(OutputIo output, bool value) =>
        Dispatcher.BeginInvoke((Action)Refresh);
}

public sealed class OutputControlRow(
    IIoService io,
    OutputIo output,
    OutputFeedback? feedback) : ObservableObject
{
    private bool _timedOut;

    public OutputIo Output { get; } = output;
    public bool HasFeedback => feedback is not null;
    public bool OutputOn => io.GetOutput(Output);
    public InputIo? FeedbackInput =>
        feedback is null
            ? null
            : OutputOn ? feedback.OnInput : feedback.OffInput;
    public bool InputOn =>
        FeedbackInput is { } input && io.GetInput(input);
    public bool TimedOut => _timedOut;

    public void ClearTimeout()
    {
        _timedOut = false;
        Refresh();
    }

    public void MarkTimeout()
    {
        _timedOut = true;
        Refresh();
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}
