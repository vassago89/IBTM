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

    public int BoltNumber
    {
        get
        {
            return Position.Bolt?.Number ?? 0;
        }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    private double _x;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    private double _y;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    private double? _z;

    public string Name
    {
        get
        {
            return Position.Bolt is { } bolt ? $"B{bolt.Number}" : Position.Target.GetDescription();
        }
    }

    public string PositionLabel
    {
        get
        {
            if (Position.Target == TeachingTarget.BoltTeaching)
                return "Add Bolt → Add Current Image → Draw ROI";
            var origin = Position.HasPosition ? Position.CoordinateOrigin?.Invoke() : null;
            return Position.Mode switch
            {
                TeachMode.Image
                    => Position.HasPosition
                        ? $"{X - (origin?.X ?? 0):F3}, {Y - (origin?.Y ?? 0):F3}"
                        : "—",
                TeachMode.XYOnly => Position.HasPosition ? $"{X:F3}, {Y:F3}" : "—",
                TeachMode.XZOnly => $"{X:F3}, {Z:F3}",
                TeachMode.XOnly => $"{X:F3}",
                TeachMode.YOnly => $"{Y:F3}",
                TeachMode.ZOnly => Z is { } z ? $"{z:F3}" : "—",
                _ => $"{X:F3}, {Y:F3}, {Z:F3}",
            };
        }
    }

    public void Teach(double x, double y, double z)
    {
        if (Position.Mode is TeachMode.Image
            or TeachMode.XYOnly
            or TeachMode.Full
            or TeachMode.XZOnly
            or TeachMode.XOnly)
        {
            X = x;
        }

        if (Position.Mode is TeachMode.Image or TeachMode.XYOnly or TeachMode.Full or TeachMode.YOnly)
        {
            Y = y;
        }

        if (Position.Mode is TeachMode.Full or TeachMode.XZOnly or TeachMode.ZOnly)
        {
            Z = z;
        }
    }

    public AxisPosition Read()
    {
        return new()
        {
            X = X,
            Y = Y,
            Z = Position.Mode == TeachMode.Image ? 0 : Z!.Value,
        };
    }

    public void Apply()
    {
        Position.Apply(Read());
    }

    public void Refresh()
    {
        var position = Position.Read();
        X = position.X;
        Y = position.Y;
        Z = position.Z;
        OnPropertyChanged(nameof(PositionLabel));
    }
}

public enum TeachingSaveBehavior
{
    [Description("Add Bolt below the teaching list. Jog to the bolt, Add Current Image, then draw its ROI on the saved image.")]
    AddBolt,

    [Description("Teach with Head 1 down; saves automatically. Move To lowers Head 1 at pickup XY, then moves Z. Vacuum is unchanged.")]
    BoltPickup,

    [Description("Calculated from this heat sink's bolt teaching and head reference pins. Move to verify at Safe Z; teach bolt positions in Inspection.")]
    BoltPosition,

    [Description("Align the pin with the live camera center, then Teach. Saves automatically.")]
    CameraCenter,

    [Description("Machine position · Teach saves automatically.")]
    Machine,
    [Description("Recipe position · Use Save Recipe after teaching.")]
    Recipe,
    [Description("Buffer setup · Apply & Save Buffer before leaving this page; otherwise staged changes are discarded.")]
    Buffer,
    [Description("Add Current Image, then draw the bolt ROI on the saved FOV. This heat sink is taught independently. Saves automatically.")]
    Image,
    [Description("Live: jog, stop, Add Current Image. Draw the barcode ROI on the saved FOV for this heat sink. Reading returns to the captured XY. Saves automatically.")]
    BarcodeFov,
}
