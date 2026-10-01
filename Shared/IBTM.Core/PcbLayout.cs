using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Data;

namespace IBTM.Core;
// Each heat sink owns its bolts with independent inspection and fastening coordinates.
public sealed class PcbLayout
{
    public PcbLayout()
    {
        BoltPoints = [];
        BindingOperations.EnableCollectionSynchronization(BoltPoints, BoltPoints);
        FasteningOrder = [];
    }

    // Old shared PCB-local coordinates must not be read as independently taught bolts.
    [JsonPropertyName("TaughtBolts")]
    public ObservableCollection<BoltPoint> BoltPoints
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (field is null)
            {
                field = value;
                return;
            }
            if (ReferenceEquals(field, value))
                return;
            // Keep the collection registered by this layout's constructor.
            field.Clear();
            foreach (var bolt in value)
                field.Add(bolt);
        }
    }

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
        var bolt = BoltPoints.FirstOrDefault(point => point.Id == boltId);
        if (bolt is null)
            return null;
        return BoltPoints.Where(point => point.HeatSink == bolt.HeatSink)
            .TakeWhile(point => point.Id != boltId).Count() + 1;
    }

    public string GetBoltName(Guid boltId, int? recordedOrdinal = null)
    {
        return BoltPoint.GetDisplayName(BoltPoints.FirstOrDefault(bolt => bolt.Id == boltId)?.Name,
            recordedOrdinal ?? GetBoltOrdinal(boltId));
    }
}
