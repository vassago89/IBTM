using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;

namespace IBTM.Device;

public sealed class IoSignals : INotifyPropertyChanged
{
    private readonly IIoService _io;
    private int _inputsAvailable;

    public IoSignals(IEnumerable<HardwareSettings> hardware, IIoService io)
    {
        _io = io;
        _inputsAvailable = io.IsReady ? 1 : 0;
        var sections = hardware.OfType<IoHardwareSettings>().ToArray();
        var inputs = new Dictionary<InputIo, IoInputStatus>();
        foreach (var section in sections)
        {
            foreach (var (signal, number) in section.Inputs)
                inputs.Add(signal, new(signal, section.Area, section.GetSection(signal), io, number));
        }
        Inputs = inputs;

        var outputs = new Dictionary<OutputIo, IoOutputStatus>();
        foreach (var section in sections)
        {
            foreach (var (signal, output) in section.Outputs)
                outputs.Add(signal, new(signal, section.Area, section.GetSection(signal), io, Inputs, output.Number));
        }
        Outputs = outputs;

        io.InputChanged += OnInputChanged;
        io.Faulted += OnIoFaulted;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool InputsAvailable => _io.IsReady;

    public IReadOnlyDictionary<InputIo, IoInputStatus> Inputs { get; }
    public IReadOnlyDictionary<OutputIo, IoOutputStatus> Outputs { get; }

    // Availability changes refresh every input, including levels unchanged on recovery.
    public void RefreshInputs()
    {
        var available = _io.IsReady ? 1 : 0;
        if (Interlocked.Exchange(ref _inputsAvailable, available) == available)
            return;
        foreach (var row in Inputs.Values)
            row.Refresh();
        PropertyChanged?.Invoke(this, new(nameof(InputsAvailable)));
    }

    // Called by the feedback monitor, never by a display worker or binding getter.
    public void RefreshOutputs()
    {
        try
        {
            foreach (var output in Outputs.Values)
            {
                if (_io.IsReady)
                    output.Refresh();
                else
                    output.Update(null);
            }
        }
        catch (IOException)
        {
            InvalidateOutputs();
            throw;
        }
    }

    public void InvalidateOutputs()
    {
        foreach (var output in Outputs.Values)
            output.Update(null);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (Inputs.TryGetValue(input, out var row))
            row.Refresh();
    }

    private void OnIoFaulted(Exception error)
    {
        RefreshInputs();
        InvalidateOutputs();
    }
}
