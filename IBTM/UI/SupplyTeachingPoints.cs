using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using static IBTM.UI.TeachingPoint;

namespace IBTM.UI;

public sealed class SupplyTeachingPoints(
    PcbBufferSettings bufferSettings,
    PcbSupplySettings supplySettings,
    PcbPlacementHandlerSettings placementSettings)
{
    public List<TeachingPoint> Build(PcbSupplyRecipe recipe) =>
        [
            Create(
                TeachingTarget.SupplyRotationZ,
                MotionGroup.PcbSupply,
                new AxisPosition { Z = supplySettings.RotationZ },
                TeachMode.ZOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyCarrierY,
                MotionGroup.PcbSupply,
                new AxisPosition { Y = supplySettings.CarrierY },
                TeachMode.YOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyPcb1Pick,
                MotionGroup.PcbSupply,
                new AxisPosition
                {
                    X = recipe.Pcb1PickPosition.X,
                    Y = supplySettings.CarrierY,
                    Z = recipe.Pcb1PickPosition.Z,
                },
                TeachMode.XZOnly),
            Create(
                TeachingTarget.SupplyPcb2Pick,
                MotionGroup.PcbSupply,
                new AxisPosition
                {
                    X = recipe.Pcb2PickPosition.X,
                    Y = supplySettings.CarrierY,
                    Z = recipe.Pcb2PickPosition.Z,
                },
                TeachMode.XZOnly),
            Create(
                TeachingTarget.SupplyBufferHandoff,
                MotionGroup.PcbSupply,
                supplySettings.BufferHandoffPosition,
                TeachMode.Full,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyBufferClearZ,
                MotionGroup.PcbSupply,
                new AxisPosition { Z = supplySettings.BufferClearZ },
                TeachMode.ZOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PlacementBufferHandoff,
                MotionGroup.PcbPlacementHandler,
                placementSettings.BufferHandoffPosition,
                TeachMode.Full,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyBufferBoundary1,
                MotionGroup.PcbSupply,
                new AxisPosition { X = bufferSettings.SupplyBoundary1 },
                TeachMode.XOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyBufferBoundary2,
                MotionGroup.PcbSupply,
                new AxisPosition { X = bufferSettings.SupplyBoundary2 },
                TeachMode.XOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PlacementBufferBoundary1,
                MotionGroup.PcbPlacementHandler,
                bufferSettings.PlacementBoundary1,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PlacementBufferBoundary2,
                MotionGroup.PcbPlacementHandler,
                bufferSettings.PlacementBoundary2,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
        ];

    public void Apply(
        PcbSupplyRecipe recipe,
        IReadOnlyCollection<TeachingPoint> points,
        TeachingPoint point)
    {
        var position = new AxisPosition
        {
            X = point.X,
            Y = point.Y,
            Z = point.Z!.Value,
        };

        switch (point.Target)
        {
            case TeachingTarget.SupplyRotationZ:
                supplySettings.RotationZ = point.Z!.Value;
                break;
            case TeachingTarget.SupplyCarrierY:
                supplySettings.CarrierY = point.Y;
                foreach (var pickPoint in points.Where(candidate =>
                             candidate.Target is TeachingTarget.SupplyPcb1Pick
                                 or TeachingTarget.SupplyPcb2Pick))
                {
                    pickPoint.Y = point.Y;
                }
                break;
            case TeachingTarget.SupplyPcb1Pick:
                recipe.Pcb1PickPosition.X = point.X;
                recipe.Pcb1PickPosition.Z = point.Z!.Value;
                break;
            case TeachingTarget.SupplyPcb2Pick:
                recipe.Pcb2PickPosition.X = point.X;
                recipe.Pcb2PickPosition.Z = point.Z!.Value;
                break;
            case TeachingTarget.SupplyBufferHandoff:
                supplySettings.BufferHandoffPosition.X = point.X;
                supplySettings.BufferHandoffPosition.Y = point.Y;
                supplySettings.BufferHandoffPosition.Z = point.Z!.Value;
                break;
            case TeachingTarget.SupplyBufferClearZ:
                supplySettings.BufferClearZ = point.Z!.Value;
                break;
            case TeachingTarget.PlacementBufferHandoff:
                placementSettings.BufferHandoffPosition.X = point.X;
                placementSettings.BufferHandoffPosition.Y = point.Y;
                placementSettings.BufferHandoffPosition.Z = point.Z!.Value;
                break;
            case TeachingTarget.SupplyBufferBoundary1:
                bufferSettings.SupplyBoundary1 = point.X;
                break;
            case TeachingTarget.SupplyBufferBoundary2:
                bufferSettings.SupplyBoundary2 = point.X;
                break;
            case TeachingTarget.PlacementBufferBoundary1:
                bufferSettings.PlacementBoundary1 = position;
                break;
            case TeachingTarget.PlacementBufferBoundary2:
                bufferSettings.PlacementBoundary2 = position;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(point),
                    point.Target,
                    null);
        }
    }

}
