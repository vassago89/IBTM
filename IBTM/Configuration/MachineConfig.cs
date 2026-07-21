namespace IBTM.Configuration;

public sealed class MachineConfig
{
    public CalibrationSettings Calibration { get; set; } = new();
    public MachineRuntimeSettings Runtime { get; set; } = new();
    public PcbPlacementOptions PcbPlacement { get; set; } = new();
    public BoltFasteningOptions BoltFastening { get; set; } = new();
    public InspectionOptions Inspection { get; set; } = new();
    public SystemSettings System { get; set; } = new();

    public void CopyFrom(MachineConfig source)
    {
        Calibration.CopyFrom(source.Calibration);
        Runtime.CopyFrom(source.Runtime);
        PcbPlacement.CopyFrom(source.PcbPlacement);
        BoltFastening.CopyFrom(source.BoltFastening);
        Inspection.CopyFrom(source.Inspection);
        System.CopyFrom(source.System);
    }

    public ZoneMotionParams GetMotionParams(int zone) => zone switch
    {
        1 => PcbPlacement.Motion,
        2 => BoltFastening.Motion,
        3 => Inspection.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(zone)),
    };

    public AxisPos ToZone1(AxisPos position) => Calibration.ToZone1(position);

    public AxisPos ToZone2(AxisPos position) => Calibration.ToZone2(position);

    public AxisPos FromZone1ToZone3(AxisPos position) =>
        Calibration.FromZone1ToZone3(position);

    public AxisPos FromZone2ToZone3(AxisPos position) =>
        Calibration.FromZone2ToZone3(position);
}

public sealed class CalibrationSettings
{
    public AxisPos Zone1Ref { get; set; } = new();
    public AxisPos Zone2Ref { get; set; } = new();
    public AxisPos Zone3Ref { get; set; } = new();
    public AxisPos Offset3To1 { get; set; } = new();
    public AxisPos Offset3To2 { get; set; } = new();

    public void CopyFrom(CalibrationSettings source)
    {
        Zone1Ref = source.Zone1Ref.Clone();
        Zone2Ref = source.Zone2Ref.Clone();
        Zone3Ref = source.Zone3Ref.Clone();
        Offset3To1 = source.Offset3To1.Clone();
        Offset3To2 = source.Offset3To2.Clone();
    }

    public void ComputeOffsets()
    {
        Offset3To1 = new AxisPos
        {
            X = Zone3Ref.X - Zone1Ref.X,
            Y = Zone3Ref.Y - Zone1Ref.Y,
            Z = Zone3Ref.Z - Zone1Ref.Z,
        };
        Offset3To2 = new AxisPos
        {
            X = Zone3Ref.X - Zone2Ref.X,
            Y = Zone3Ref.Y - Zone2Ref.Y,
            Z = Zone3Ref.Z - Zone2Ref.Z,
        };
    }

    public AxisPos ToZone1(AxisPos position) => new()
    {
        X = position.X - Offset3To1.X,
        Y = position.Y - Offset3To1.Y,
        Z = position.Z - Offset3To1.Z,
    };

    public AxisPos ToZone2(AxisPos position) => new()
    {
        X = position.X - Offset3To2.X,
        Y = position.Y - Offset3To2.Y,
        Z = position.Z - Offset3To2.Z,
    };

    public AxisPos FromZone1ToZone3(AxisPos position) => new()
    {
        X = position.X + Offset3To1.X,
        Y = position.Y + Offset3To1.Y,
        Z = position.Z + Offset3To1.Z,
    };

    public AxisPos FromZone2ToZone3(AxisPos position) => new()
    {
        X = position.X + Offset3To2.X,
        Y = position.Y + Offset3To2.Y,
        Z = position.Z + Offset3To2.Z,
    };
}

public sealed class SystemSettings
{
    public string Language { get; set; } = "en";

    public void CopyFrom(SystemSettings source)
    {
        Language = source.Language;
    }
}
