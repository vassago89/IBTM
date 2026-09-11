using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace IBTM.Core;
// Each heat sink owns its taught bolts; coordinates are relative to the carrier reference pin.
public sealed class PcbLayout
{
    // Old shared PCB-local coordinates must not be read as independent carrier coordinates.
    [JsonPropertyName("TaughtBolts")]
    public List<BoltPoint> BoltPoints { get; set; } = [];

    public IEnumerable<BoltTarget> GetBolts()
    {
        return BoltPoints.Select(point => new BoltTarget(point));
    }

    public IEnumerable<BoltTarget> GetBolts(HeatSinkSlot pcb)
    {
        return GetBolts().Where(bolt => bolt.HeatSink == pcb);
    }

}

public sealed record BoltTarget(BoltPoint Point)
{
    public HeatSinkSlot HeatSink
    {
        get
        {
            return Point.HeatSink;
        }
    }
    public int Number
    {
        get
        {
            return Point.Number;
        }
    }

    public FasteningHead Head
    {
        get
        {
            return Point.Head;
        }
    }

    public double? X
    {
        get
        {
            return Point.X;
        }
    }

    public double? Y
    {
        get
        {
            return Point.Y;
        }
    }
}
