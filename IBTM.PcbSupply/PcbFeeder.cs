using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbFeeder(
    MotionService motion,
    IIoService io,
    PcbHandoff handoff,
    PcbSupplySettings settings,
    ProcessEvents events)
{
    public void Initialize()
    {
        motion.Initialize();
        io.SetOutput(OutputIo.PcbSupplyReady, false);
        io.SetOutput(OutputIo.PcbSupplyGripper, false);
        io.SetOutput(OutputIo.PcbSupplyRotateToHandoff, false);
    }

    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            io.SetOutput(OutputIo.PcbSupplyReady, true);
            try
            {
                await io.WaitForInputAsync(
                    InputIo.PcbSupplyCarrierAvailable,
                    true,
                    cancellationToken);
            }
            finally
            {
                io.SetOutput(OutputIo.PcbSupplyReady, false);
            }

            await io.WaitForInputAsync(
                InputIo.PcbSupplyCarrierAvailable,
                false,
                cancellationToken);
            await events.RunStageAsync(
                ProcessStage.SupplyPcb,
                cancellationToken,
                token => SupplyAsync(recipe.CarrierPick1, token));
            await events.RunStageAsync(
                ProcessStage.SupplyPcb,
                cancellationToken,
                token => SupplyAsync(recipe.CarrierPick2, token));
        }
    }

    public void Stop()
    {
        motion.Stop();
        io.SetOutput(OutputIo.PcbSupplyReady, false);
    }

    public void EmergencyStop() => motion.EmergencyStop();

    private async Task SupplyAsync(
        XzPos pickPosition,
        CancellationToken cancellationToken)
    {
        await io.WaitForOutputFeedbackAsync(
            OutputIo.PcbSupplyRotateToHandoff,
            false,
            cancellationToken);
        await motion.MoveToXZAsync(
            pickPosition.X,
            pickPosition.Z,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyGripper,
            true,
            cancellationToken);

        if (!io.GetInput(InputIo.PcbSupplyPcbPresent))
        {
            await io.SetOutputAndWaitAsync(
                OutputIo.PcbSupplyGripper,
                false,
                cancellationToken);
            await motion.MoveToSafeZAsync(cancellationToken);
            await handoff.OfferAsync(false, cancellationToken);
            return;
        }

        await motion.MoveToSafeZAsync(cancellationToken);

        await motion.MoveToXAsync(
            settings.RotationX,
            settings.Motion.HorizontalSpeed,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyRotateToHandoff,
            true,
            cancellationToken);

        var handoffPosition = settings.HandoffPosition;
        await motion.MoveToXZAsync(
            handoffPosition.X,
            handoffPosition.Z,
            cancellationToken);

        await handoff.OfferAsync(true, cancellationToken);
        await handoff.WaitForPickupAsync(cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyGripper,
            false,
            cancellationToken);
        await handoff.WaitUntilClearAsync(cancellationToken);

        await motion.MoveToXAsync(
            settings.RotationX,
            settings.Motion.HorizontalSpeed,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyRotateToHandoff,
            false,
            cancellationToken);
    }
}
