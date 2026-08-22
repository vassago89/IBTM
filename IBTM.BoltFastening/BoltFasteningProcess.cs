using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningProcess(BoltFasteningStation station)
{
    public async Task RunAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await station.WaitForBackupPlateAsync(
                    true,
                    cancellationToken);
                var bolts = recipe.BoltPoints
                    .OrderBy(bolt => bolt.Number)
                    .ToArray();

                foreach (var bolt in bolts)
                {
                    if (!station.HousingPresent(bolt.Housing))
                    {
                        continue;
                    }

                    var result = await station.FastenAsync(
                        bolt,
                        cancellationToken);
                    if (!result.Success)
                    {
                        return;
                    }
                }

                await station.MoveToSafeZAsync(cancellationToken);
                await station.WaitForBackupPlateAsync(
                    false,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
