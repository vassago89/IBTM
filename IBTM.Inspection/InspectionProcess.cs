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
        if (work.CarrierPresent && !work.Completed)
        {
            work.RestartInspection();
        }

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
                        var bolt = NextBolt(bolts);
                        switch (State(bolt))
                        {
                            case InspectionProcessState.MovingToBolt:
                                await imageCapture.MoveToAsync(
                                    bolt!,
                                    stateOperation.Token);
                                break;

                            case InspectionProcessState.InspectingBolt:
                                Inspect(bolt!);
                                break;

                            case InspectionProcessState.CompletingCarrier:
                                CompleteInspection();
                                break;
                        }
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

    public InspectionProcessState State(
        IReadOnlyList<BoltPoint> bolts) =>
        State(NextBolt(bolts));

    public BoltPoint? ActiveBolt(IReadOnlyList<BoltPoint> bolts) =>
        work.State == InspectionState.ReadyToInspect
            ? NextBolt(bolts)
            : null;

    private InspectionProcessState State(BoltPoint? bolt)
    {
        if (work.State != InspectionState.ReadyToInspect)
        {
            return InspectionProcessState.Waiting;
        }

        if (bolt is null)
        {
            return InspectionProcessState.CompletingCarrier;
        }

        return imageCapture.IsAt(bolt)
            ? InspectionProcessState.InspectingBolt
            : InspectionProcessState.MovingToBolt;
    }

    private BoltPoint? NextBolt(
        IReadOnlyList<BoltPoint> bolts) =>
        bolts
            .Where(bolt => work.HeatSinkPresent(bolt.HeatSink))
            .OrderBy(bolt => bolt.Number)
            .FirstOrDefault(bolt => !Inspected(bolt));

    private bool Inspected(BoltPoint bolt) =>
        work.Assemblies.Any(assembly =>
            assembly.HeatSink == bolt.HeatSink
            && assembly.BoltPresenceResults.ContainsKey(bolt.Number));

    private void Inspect(BoltPoint bolt)
    {
        var image = imageCapture.Capture();
        work.Assembly(bolt.HeatSink).RecordBoltPresence(
            bolt.Number,
            inspector.IsPresent(image));
    }

    private void CompleteInspection()
    {
        foreach (var heatSink in Enum.GetValues<HeatSinkSlot>()
                     .Where(work.HeatSinkPresent))
        {
            work.Assembly(heatSink).CompleteInspection();
        }

        work.Complete();
    }
}
