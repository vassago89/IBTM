using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace IBTM.Core;
// Each heat sink owns its bolts with independent inspection and fastening coordinates.
public sealed class PcbLayout
{
    public PcbLayout()
    {
        BoltPoints = [];
        FasteningOrder = [];
    }

    // Old shared PCB-local coordinates must not be read as independently taught bolts.
    [JsonPropertyName("TaughtBolts")]
    public List<BoltPoint> BoltPoints { get; set; }

    public List<Guid> FasteningOrder { get; set; }

    [JsonIgnore]
    public IEnumerable<BoltPoint> FasteningPoints
    {
        get
        {
            return BoltPoints.OrderBy(bolt => bolt.Head == FasteningHead.Shooting ? 0 : 1)
                .ThenBy(bolt => bolt.HeatSink)
                .ThenBy(bolt =>
                {
                    // New points follow the configured points for the same PCB and head.
                    var index = FasteningOrder.IndexOf(bolt.Id);
                    return index >= 0 ? index : int.MaxValue;
                });
        }
    }

    public int? GetBoltOrdinal(Guid boltId)
    {
        var bolt = BoltPoints.SingleOrDefault(point => point.Id == boltId);
        if (bolt is null)
            return null;
        return BoltPoints.Where(point => point.HeatSink == bolt.HeatSink)
            .TakeWhile(point => point.Id != boltId).Count() + 1;
    }

    public IEnumerable<BoltPoint> GetBolts(HeatSinkSlot pcb)
    {
        return BoltPoints.Where(bolt => bolt.HeatSink == pcb);
    }
}
