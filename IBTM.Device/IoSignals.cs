using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IBTM.Device;

public sealed class IoSignals
{
    private readonly IIoService _io;

    public IoSignals(IEnumerable<HardwareSettings> hardware, IIoService io)
    {
        _io = io;
        var sections = hardware.ToArray();
        Inputs = sections.OfType<InputHardwareSettings>()
            .SelectMany(section => section.Inputs.Keys.Select(input => new IoSignal<InputIo>(
                input, section.Area, section.GetSection(input), io.GetInput)))
            .ToDictionary(row => row.Signal);
        Outputs = sections.OfType<IoHardwareSettings>()
            .SelectMany(section => section.Outputs.Keys.Select(output => new IoOutputStatus(
                output, section.Area, section.GetSection(output), io, Inputs)))
            .ToDictionary(row => row.Signal);

        io.InputChanged += (input, _) =>
        {
            if (Inputs.TryGetValue(input, out var row)) row.Refresh();
        };
    }

    public IReadOnlyDictionary<InputIo, IoSignal<InputIo>> Inputs { get; }
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
