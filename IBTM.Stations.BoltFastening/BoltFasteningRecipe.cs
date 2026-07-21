namespace IBTM.Stations.BoltFastening;

public sealed class BoltPoint
{
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double TargetTorqueNm { get; set; }
}

public sealed class BoltFasteningRecipe
{
    public AxisPos FiducialPosition { get; set; } = new() { X = 70.0, Y = 85.0, Z = 10.0 };
    public double Pcb1CenterX { get; set; } = 75.0;
    public double Pcb2CenterX { get; set; } = 130.0;
    public double PcbCenterY { get; set; } = 95.0;
    public List<BoltPoint> BoltPoints { get; set; } =
    [
        new() { Name = "B1", X = -10.0, Y = -10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B2", X = 10.0, Y = -10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B3", X = 10.0, Y = 10.0, Z = 20.0, TargetTorqueNm = 15.0 },
        new() { Name = "B4", X = -10.0, Y = 10.0, Z = 20.0, TargetTorqueNm = 15.0 },
    ];
}
