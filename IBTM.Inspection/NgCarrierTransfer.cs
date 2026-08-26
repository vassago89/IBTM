using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class NgCarrierTransfer(
    IIoService io,
    IXyMotion motion,
    InspectionGantrySettings settings)
{
    public bool HoldingCarrier =>
        io.GetInput(InputIo.NgCarrierJigDetected);

    public async Task PickAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(
            io.SetOutputAndWaitAsync(
                OutputIo.NgCarrierPickupDown,
                false,
                cancellationToken),
            io.SetOutputAndWaitAsync(
                OutputIo.NgCarrierGripperClose,
                false,
                cancellationToken));
        await MoveToAsync(
            settings.NgCarrierJigPickupPosition,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierPickupDown,
            true,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierGripperClose,
            true,
            cancellationToken);
        await io.WaitForInputAsync(
            InputIo.NgCarrierJigDetected,
            true,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierPickupDown,
            false,
            cancellationToken);
    }

    public async Task PlaceOnShuttleAsync(
        CancellationToken cancellationToken = default)
    {
        await MoveToAsync(
            settings.NgShuttlePlacePosition,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierPickupDown,
            true,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierGripperClose,
            false,
            cancellationToken);
        await io.WaitForInputAsync(
            InputIo.NgCarrierJigDetected,
            false,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierPickupDown,
            false,
            cancellationToken);
    }

    private Task MoveToAsync(
        AxisPos position,
        CancellationToken cancellationToken) =>
        motion.MoveToXYAsync(
            position.X,
            position.Y,
            settings.Motion.HorizontalSpeed,
            cancellationToken);
}
