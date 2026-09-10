using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IBTM.Device;

public sealed class IoSignals
{
    private readonly IIoService _io;
    private int _inputsAvailable;

    public IoSignals(IEnumerable<HardwareSettings> hardware, IIoService io)
    {
        _io = io;
        _inputsAvailable = io.IsReady ? 1 : 0;
        var sections = hardware.ToArray();
        Inputs = sections.OfType<InputHardwareSettings>()
            .SelectMany(section => section.Inputs.Keys.Select(input => new IoInputStatus(
                input, section.Area, section.GetSection(input), io, section.Inputs[input])))
            .ToDictionary(row => row.Signal);
        Outputs = sections.OfType<IoHardwareSettings>()
            .SelectMany(section => section.Outputs.Keys.Select(output => new IoOutputStatus(
                output, section.Area, section.GetSection(output), io, Inputs,
                section.Outputs[output].Number, section.Outputs[output].OffNumber)))
            .ToDictionary(row => row.Signal);

        io.InputChanged += (input, _) =>
        {
            if (Inputs.TryGetValue(input, out var row)) row.Refresh();
        };
        io.Faulted += _ => RefreshInputs();
    }

    public bool InputsAvailable => _io.IsReady;
    public event Action? InputAvailabilityChanged;

    // On reconnection the initial scan can replace the cache without DI change events.
    public void RefreshInputs()
    {
        var available = _io.IsReady ? 1 : 0;
        if (Interlocked.Exchange(ref _inputsAvailable, available) == available) return;
        foreach (var row in Inputs.Values) row.Refresh();
        InputAvailabilityChanged?.Invoke();
    }

    public IReadOnlyDictionary<InputIo, IoInputStatus> Inputs { get; }
    public IReadOnlyDictionary<OutputIo, IoOutputStatus> Outputs { get; }

    // Called by the shared display worker, never by a binding getter.
    public void RefreshOutputs()
    {
        try
        {
            foreach (var output in Outputs.Values)
            {
                if (_io.IsReady) output.Refresh();
                else output.Update(null);
            }
        }
        catch (IOException)
        {
            foreach (var output in Outputs.Values) output.Update(null);
            throw;
        }
    }

    public IoStatus Select(
        HardwareArea area,
        IEnumerable<InputIo> inputs,
        IEnumerable<OutputIo> outputs) =>
        new(area, inputs.Select(input => Inputs[input]), outputs.Select(output => Outputs[output]));
}
