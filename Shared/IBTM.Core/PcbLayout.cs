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
    }

    // Old shared PCB-local coordinates must not be read as independently taught bolts.
    [JsonPropertyName("TaughtBolts")]
    public List<BoltPoint> BoltPoints { get; set; }

    public IEnumerable<BoltPoint> GetBolts(HeatSinkSlot pcb)
    {
        return BoltPoints.Where(bolt => bolt.HeatSink == pcb);
    }
}
