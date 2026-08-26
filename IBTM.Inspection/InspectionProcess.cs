using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class InspectionProcess(
    InspectionWork work,
    BoltImageCapture imageCapture,
    BoltPresenceInspector inspector)
{
    public async Task RunAsync(
        IReadOnlyList<BoltPoint> bolts,
        CancellationToken cancellationToken = default)
    {
        using var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        work.Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (work.State != InspectionState.ReadyToInspect)
                {
                    await stateChanged.WaitAsync(cancellationToken);
                    continue;
                }

                using var stateOperation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                void ReevaluateState() => stateOperation.Cancel();

                work.Changed += ReevaluateState;
                try
                {
                    if (work.State == InspectionState.ReadyToInspect)
                    {
                        await InspectCarrierAsync(
                            bolts,
                            stateOperation.Token);
                    }
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    work.Changed -= ReevaluateState;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            work.Changed -= OnStateChanged;
        }
    }

    private async Task InspectCarrierAsync(
        IReadOnlyList<BoltPoint> boltPoints,
        CancellationToken cancellationToken)
    {
        var housings = Enum.GetValues<HousingSlot>()
            .Where(work.HousingPresent)
            .ToArray();
        if (housings.Length == 0)
        {
            work.Complete();
            return;
        }

        var bolts = boltPoints
            .Where(bolt => work.HousingPresent(bolt.Housing))
            .OrderBy(bolt => bolt.Number)
            .ToArray();
        foreach (var housing in housings)
        {
            if (!bolts.Any(bolt => bolt.Housing == housing))
            {
                throw new InvalidOperationException(
                    $"{housing} has no inspection bolt points.");
            }

            work.Assembly(housing).BeginInspection();
        }

        var images = await imageCapture.CaptureAsync(
            bolts,
            cancellationToken);
        foreach (var capture in images)
        {
            work.Assembly(capture.Point.Housing).RecordBoltPresence(
                capture.Point.Number,
                inspector.IsPresent(capture.Image));
        }

        foreach (var housing in housings)
        {
            work.Assembly(housing).CompleteInspection();
        }

        cancellationToken.ThrowIfCancellationRequested();
        work.Complete();
    }
}
