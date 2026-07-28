using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Transport;

public sealed class CarrierJigPositioner(
    IIoService io,
    InputIo presentInput,
    OutputIo stopperUpOutput,
    OutputIo backupPlateUpOutput)
{
    public void Initialize()
    {
        io.SetOutput(stopperUpOutput, false);
        io.SetOutput(backupPlateUpOutput, false);
    }

    public async Task PositionAsync(CancellationToken cancellationToken)
    {
        await WaitUntilPresentAsync(cancellationToken);
        await io.SetOutputAndWaitAsync(
            backupPlateUpOutput,
            true,
            cancellationToken);
    }

    public Task WaitUntilPresentAsync(CancellationToken cancellationToken) =>
        io.WaitForInputAsync(presentInput, true, cancellationToken);

    public async Task WaitUntilEmptyAsync(CancellationToken cancellationToken)
    {
        await WaitUntilCarrierLeavesAsync(cancellationToken);
        await io.WaitForOutputFeedbackAsync(
            backupPlateUpOutput,
            false,
            cancellationToken);
        await io.WaitForOutputFeedbackAsync(
            stopperUpOutput,
            false,
            cancellationToken);
    }

    public async Task CompleteRemovalAsync(CancellationToken cancellationToken)
    {
        await WaitUntilCarrierLeavesAsync(cancellationToken);
        await io.SetOutputAndWaitAsync(
            backupPlateUpOutput,
            false,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            stopperUpOutput,
            false,
            cancellationToken);
    }

    internal async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await io.SetOutputAndWaitAsync(
            backupPlateUpOutput,
            false,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            stopperUpOutput,
            true,
            cancellationToken);
        await WaitUntilCarrierLeavesAsync(cancellationToken);
        await io.SetOutputAndWaitAsync(
            stopperUpOutput,
            false,
            cancellationToken);
    }

    private Task WaitUntilCarrierLeavesAsync(CancellationToken cancellationToken) =>
        io.WaitForInputAsync(presentInput, false, cancellationToken);
}
