using System.Collections.Generic;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Stations.BoltFastening;

[JsonConverter(typeof(JsonStringEnumConverter<BoltType>))]
public enum BoltType
{
    Standard,
    Loctite,
}

public sealed class BoltPoint
{
    public int Number { get; set; }
    [JsonIgnore] public string Name => $"B{Number}";
    public BoltType BoltType { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double TargetTorqueNm { get; set; }
}

public sealed class BoltFasteningRecipe
{
    public AxisPos Pcb1Reference { get; set; } = new() { X = 75.0, Y = 95.0 };
    public AxisPos Pcb2Reference { get; set; } = new() { X = 130.0, Y = 95.0 };
    public List<BoltPoint> BoltPoints { get; set; } =
    [
        new() { Number = 1, X = -10.0, Y = -10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Number = 2, X = 10.0, Y = -10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Number = 3, X = 10.0, Y = 10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Number = 4, X = -10.0, Y = 10.0, Z = 20.0, TargetTorqueNm = 15.0 },
    ];
}
