using System.Collections.Generic;
using System.Linq;

namespace IBTM.Device;

public sealed class IoSignals
{
    public IoSignals(IEnumerable<HardwareSettings> hardware, IIoService io)
    {
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
        io.OutputChanged += (output, _) =>
        {
            if (Outputs.TryGetValue(output, out var row)) row.Refresh();
        };
    }

    public IReadOnlyDictionary<InputIo, IoSignal<InputIo>> Inputs { get; }
    public IReadOnlyDictionary<OutputIo, IoOutputStatus> Outputs { get; }

    public IoStatus Select(
        HardwareArea area,
        IEnumerable<InputIo> inputs,
        IEnumerable<OutputIo> outputs) =>
        new(area, inputs.Select(input => Inputs[input]), outputs.Select(output => Outputs[output]));
}
