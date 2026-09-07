using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class TeachingPoint : ObservableObject
{
    public TeachingPoint(TeachingPosition position)
    {
        Position = position;
        Refresh();
    }

    public TeachingPosition Position { get; }
    public TeachingTarget Target => Position.Target;
    public MotionGroup MotionGroup => Position.MotionGroup;
    public TeachMode TeachMode => Position.Mode;
    public TeachingStorage Storage => Position.Storage;
    public int BoltNumber => Position.Bolt?.Number ?? 0;
    public HeatSinkSlot? HeatSink => Position.Bolt?.HeatSink;
    public FasteningHead? Head => Position.Bolt?.Head;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))] private double _x;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))] private double _y;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))] private double? _z;

    public string Name => Position.Bolt is { } bolt ? $"B{bolt.Number}" : Target.GetDescription();

    public string PositionLabel => TeachMode switch
    {
        TeachMode.Image => Position.Bolt is { X: { } x, Y: { } y } ? $"{x:F3}, {y:F3}" : "—",
        TeachMode.XYOnly => Position.HasPosition ? $"{X:F3}, {Y:F3}" : "—",
        TeachMode.XZOnly => $"{X:F3}, {Z:F3}",
        TeachMode.XOnly => $"{X:F3}",
        TeachMode.YOnly => $"{Y:F3}",
        TeachMode.ZOnly => Z is { } z ? $"{z:F3}" : "—",
        _ => $"{X:F3}, {Y:F3}, {Z:F3}",
    };

    public void Teach(double x, double y, double z)
    {
        if (TeachMode is TeachMode.Image or TeachMode.XYOnly
            or TeachMode.Full or TeachMode.XZOnly or TeachMode.XOnly)
        {
            X = x;
        }

        if (TeachMode is TeachMode.Image or TeachMode.XYOnly
            or TeachMode.Full or TeachMode.YOnly)
        {
            Y = y;
        }

        if (TeachMode is TeachMode.Full or TeachMode.XZOnly or TeachMode.ZOnly)
        {
            Z = z;
        }
    }

    public void Apply() => Position.Apply(new AxisPosition
    {
        X = X,
        Y = Y,
        Z = TeachMode == TeachMode.Image ? 0 : Z!.Value,
    });

    public void Refresh()
    {
        var position = Position.Read();
        X = position.X;
        Y = position.Y;
        Z = Position.Bolt is { } bolt ? bolt.Z : position.Z;
        OnPropertyChanged(nameof(PositionLabel));
    }
}

public enum TeachingSaveBehavior
{
    [Description("Align the pin with the live camera center, then Teach. Saves automatically.")]
    CameraCenter,

    [Description("Machine position · Teach saves automatically.")]
    Machine,
    [Description("Recipe position · Use Save Recipe after teaching.")]
    Recipe,
    [Description("Buffer setup · Apply & Save Buffer before leaving this page; otherwise staged changes are discarded.")]
    Buffer,
    [Description("Image point · Click the image to teach and save automatically.")]
    Image,
}
