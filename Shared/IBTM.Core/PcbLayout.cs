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

    public IEnumerable<BoltPoint> GetBolts(HeatSinkSlot pcb)
    {
        return BoltPoints.Where(bolt => bolt.HeatSink == pcb);
    }
}
