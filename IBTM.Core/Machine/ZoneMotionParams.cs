namespace IBTM.Core.Machine;

public sealed class ZoneMotionParams
{
    public double SpeedXY { get; set; } = 100.0;
    public double SpeedZ { get; set; } = 50.0;
    public double Acceleration { get; set; } = 500.0;
    public double Deceleration { get; set; } = 500.0;
    public double SoftLimitXPlus { get; set; } = 300.0;
    public double SoftLimitXMinus { get; set; }
    public double SoftLimitYPlus { get; set; } = 200.0;
    public double SoftLimitYMinus { get; set; }
    public double SoftLimitZPlus { get; set; } = 100.0;
    public double SoftLimitZMinus { get; set; }
    public double HomeOffsetX { get; set; }
    public double HomeOffsetY { get; set; }
    public double HomeOffsetZ { get; set; }

    public void CopyFrom(ZoneMotionParams source)
    {
        SpeedXY = source.SpeedXY;
        SpeedZ = source.SpeedZ;
        Acceleration = source.Acceleration;
        Deceleration = source.Deceleration;
        SoftLimitXPlus = source.SoftLimitXPlus;
        SoftLimitXMinus = source.SoftLimitXMinus;
        SoftLimitYPlus = source.SoftLimitYPlus;
        SoftLimitYMinus = source.SoftLimitYMinus;
        SoftLimitZPlus = source.SoftLimitZPlus;
        SoftLimitZMinus = source.SoftLimitZMinus;
        HomeOffsetX = source.HomeOffsetX;
        HomeOffsetY = source.HomeOffsetY;
        HomeOffsetZ = source.HomeOffsetZ;
    }
}
