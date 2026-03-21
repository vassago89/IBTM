namespace IBTM.Device;

/// <summary>모션 위치 변경 이벤트 인자 (마이크로미터 단위)</summary>
public class MotionPositionEventArgs : EventArgs
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public MotionPositionEventArgs(double x, double y, double z)
    {
        X = x; Y = y; Z = z;
    }
}
