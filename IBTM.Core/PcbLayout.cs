using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace IBTM.Core;

// One PCB pattern, placed twice relative to the carrier's upper-left locating pin.
public sealed class PcbLayout
{
    public double Width { get; set; }
    public double Height { get; set; }
    public Dictionary<HeatSinkSlot, AxisPosition> Origins { get; set; } = [];
    public List<BoltPoint> BoltPoints { get; set; } = [];
    public PcbRegion? DataMatrix { get; set; }

    [JsonIgnore]
    public bool IsDefined => Width > 0 && Height > 0
        && Enum.GetValues<HeatSinkSlot>().All(Origins.ContainsKey);

    public IEnumerable<BoltTarget> GetBolts() =>
        Enum.GetValues<HeatSinkSlot>().SelectMany(GetBolts);

    public IEnumerable<BoltTarget> GetBolts(HeatSinkSlot pcb) =>
        BoltPoints.Select(point => new BoltTarget(point, pcb, this));

    public PcbRegion? GetRegion(HeatSinkSlot pcb) =>
        Width > 0 && Height > 0 && Origins.TryGetValue(pcb, out var origin)
            ? new(origin.X, origin.Y, Width, Height) : null;

    public PcbRegion? GetDataMatrix(HeatSinkSlot pcb) =>
        DataMatrix is { } region && Origins.TryGetValue(pcb, out var origin)
            ? region with { X = region.X + origin.X, Y = region.Y + origin.Y } : null;
}

public sealed record PcbRegion(double X, double Y, double Width, double Height)
{
    [JsonIgnore]
    public AxisPosition Center => new() { X = X + Width / 2, Y = Y + Height / 2 };
}

// A physical target reads the shared point and PCB origin; it stores no copied coordinates.
public sealed record BoltTarget(BoltPoint Point, HeatSinkSlot HeatSink, PcbLayout Layout)
{
    public int Number => Point.Number;
    public FasteningHead Head => Point.Head;
    public double? X => Point.X + Layout.Origins.GetValueOrDefault(HeatSink)?.X;
    public double? Y => Point.Y + Layout.Origins.GetValueOrDefault(HeatSink)?.Y;
}
