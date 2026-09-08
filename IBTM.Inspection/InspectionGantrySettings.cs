using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantrySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();

    public TeachingPosition[] GetTeachingPositions(CarrierReferenceSettings reference) =>
    [
        new(TeachingTarget.CarrierUpperLeftLocatingPin, MotionGroup.InspectionGantry, TeachMode.XYOnly,
            () => reference.UpperLeftLocatingPin ?? new(), p => reference.UpperLeftLocatingPin = p,
            reference, () => reference.UpperLeftLocatingPin is not null),
        new(TeachingTarget.CarrierLowerRightLocatingPin, MotionGroup.InspectionGantry, TeachMode.XYOnly,
            () => reference.LowerRightLocatingPin ?? new(), p => reference.LowerRightLocatingPin = p,
            reference, () => reference.LowerRightLocatingPin is not null),
    ];

    public IEnumerable<TeachingPosition> GetBoltTeachingPositions(
        IEnumerable<BoltTarget> bolts, CarrierReferenceSettings reference) =>
        bolts.Select(bolt => new TeachingPosition(
            TeachingTarget.BoltReference, MotionGroup.InspectionGantry, TeachMode.Image,
            () => HasTeachingPosition(bolt, reference) ? GetBoltPosition(bolt, reference) : new(),
            p =>
            {
                var position = CarrierCoordinates.FromMachine(p, reference.UpperLeftLocatingPin!);
                var origin = bolt.Layout.Origins[bolt.HeatSink];
                bolt.Point.X = position.X - origin.X;
                bolt.Point.Y = position.Y - origin.Y;
            },
            isDefined: () => HasTeachingPosition(bolt, reference))
        {
            Bolt = bolt,
            CoordinateOrigin = () => CarrierCoordinates.ToMachine(
                bolt.Layout.Origins[bolt.HeatSink], reference.UpperLeftLocatingPin!),
        });

    public TeachingPosition[] GetPcbTeachingPositions(
        PcbLayout layout, HeatSinkSlot pcb, CarrierReferenceSettings reference) =>
    [
        new(TeachingTarget.PcbRegion, MotionGroup.InspectionGantry, TeachMode.Image,
            () => reference.IsDefined && layout.Origins.TryGetValue(pcb, out var origin)
                ? CarrierCoordinates.ToMachine(origin, reference.UpperLeftLocatingPin!) : new(),
            p => layout.Origins[pcb] = CarrierCoordinates.FromMachine(p, reference.UpperLeftLocatingPin!),
            isDefined: () => reference.IsDefined && layout.GetRegion(pcb) is not null)
        {
            CoordinateOrigin = () => reference.UpperLeftLocatingPin!,
        },
        new(TeachingTarget.DataMatrix, MotionGroup.InspectionGantry, TeachMode.Image,
            () => reference.IsDefined && layout.GetDataMatrix(pcb) is { } region
                ? CarrierCoordinates.ToMachine(region.Center, reference.UpperLeftLocatingPin!) : new(),
            p =>
            {
                var center = CarrierCoordinates.FromMachine(p, reference.UpperLeftLocatingPin!);
                var origin = layout.Origins[pcb];
                var region = layout.DataMatrix!;
                layout.DataMatrix = region with
                {
                    X = center.X - origin.X - region.Width / 2,
                    Y = center.Y - origin.Y - region.Height / 2,
                };
            }, isDefined: () => reference.IsDefined && layout.GetDataMatrix(pcb) is not null)
        {
            CoordinateOrigin = () => CarrierCoordinates.ToMachine(layout.Origins[pcb], reference.UpperLeftLocatingPin!),
        },
    ];

    private static bool HasTeachingPosition(BoltTarget bolt, CarrierReferenceSettings reference) =>
        reference.IsDefined && bolt is { X: not null, Y: not null };

    public AxisPosition GetBoltPosition(
        BoltTarget bolt,
        CarrierReferenceSettings reference) =>
        CarrierCoordinates.ToMachine(
            new AxisPosition
            {
                X = bolt.X!.Value,
                Y = bolt.Y!.Value,
            },
            reference.UpperLeftLocatingPin!);
}
