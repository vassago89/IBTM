using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Inspection;

public enum InspectionDryRunState
{
    [Description("Inspection gantry is not ready")] Unavailable,
    [Description("Waiting for carrier")] WaitingForCarrier,
    [Description("Raise the backup plate and lower the stopper")] WaitingForSeat,
    [Description("Raise and clear the NG pickup")] WaitingForGantry,
    [Description("Ready to inspect")] Ready,
    [Description("Moving to Data Matrix")] MovingToBarcode,
    [Description("Reading Data Matrix")] ReadingBarcode,
    [Description("Moving to bolt")] MovingToBolt,
    [Description("Inspecting bolt")] InspectingBolt,
}

public enum InspectionRouteDirection
{
    [Description("Forward")] Forward,
    [Description("Return")] Return,
}

// Repeats the taught route without completing or releasing the production carrier.
public sealed class InspectionDryRun : AutoUnit
{
    private readonly InspectionWork _work;
    private readonly NgCarrierTransfer _transfer;
    private readonly BoltInspector _inspector;
    private readonly Func<PcbLayout> _getPcb;
    private Target[] _targets = [];
    private Target? _current;

    public InspectionDryRun(InspectionWork work, NgCarrierTransfer transfer,
        InspectionGantry gantry, BoltInspector inspector, Func<PcbLayout> getPcb)
    {
        _work = work;
        _transfer = transfer;
        _inspector = inspector;
        _getPcb = getPcb;
        work.Changed += NotifyChanged;
        transfer.Changed += NotifyChanged;
        gantry.Feedback.StateChanged += NotifyChanged;
    }

    public override event Action? Changed;
    public bool Ready => _work.CarrierSeated && _transfer.IsClear;
    public InspectionRouteDirection Direction { get; private set; }
    public int CompletedPasses { get; private set; }
    public HeatSinkSlot? ActivePcb => _current?.Pcb;
    public int? ActiveBolt => _current?.Bolt?.Number;
    public string? LastBarcode { get; private set; }
    public bool? LastBoltPresent { get; private set; }

    public InspectionDryRunState State => !_work.CarrierPresent ? InspectionDryRunState.WaitingForCarrier
        : !_work.CarrierSeated ? InspectionDryRunState.WaitingForSeat
        : !_transfer.IsClear ? InspectionDryRunState.WaitingForGantry
        : _current is not { } target ? InspectionDryRunState.Ready
        : target.Bolt is { } invalidBolt && !_inspector.HasPosition(invalidBolt)
            || target.Bolt is null && !_inspector.HasBarcodeRegion(target.Pcb) ? InspectionDryRunState.Ready
        : target.Bolt is { } bolt
            ? _inspector.IsAt(bolt) ? InspectionDryRunState.InspectingBolt : InspectionDryRunState.MovingToBolt
            : _inspector.IsAtBarcode(target.Pcb) ? InspectionDryRunState.ReadingBarcode : InspectionDryRunState.MovingToBarcode;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = _current;
        var pcb = _getPcb();
        var targets = Enum.GetValues<HeatSinkSlot>().Where(_work.HeatSinkPresent)
            .SelectMany(slot => new[] { new Target(slot, null) }.Concat(
                pcb.GetBolts(slot).OrderBy(bolt => bolt.Number).Select(bolt => new Target(slot, bolt))))
            .ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("Inspection dry run requires a detected heat sink.");
        if (targets.Any(target => target.Bolt is { } bolt
                ? !_inspector.HasPosition(bolt) : !_inspector.HasBarcodeRegion(target.Pcb)))
            throw new InvalidOperationException("Teach the carrier reference, bolt positions and Data Matrix region before inspection dry run.");

        var next = targets.FirstOrDefault(target => target.Pcb == previous?.Pcb
            && target.Bolt?.Number == previous?.Bolt?.Number);
        if (next is null) Direction = InspectionRouteDirection.Forward;
        _targets = targets;
        _current = next ?? targets[0];

        await Task.Run(_inspector.CheckReady, cancellationToken);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckCarrier() { if (!Ready) operation.Cancel(); }
        _work.Changed += CheckCarrier;
        _transfer.Changed += CheckCarrier;
        try
        {
            CheckCarrier();
            NotifyChanged();
            await RunLoopAsync(ExecuteAsync, operation.Token);
        }
        finally
        {
            _work.Changed -= CheckCarrier;
            _transfer.Changed -= CheckCarrier;
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var target = _current!;
        switch (State)
        {
            case InspectionDryRunState.MovingToBarcode:
                await _inspector.MoveToBarcodeAsync(target.Pcb, cancellationToken);
                return;
            case InspectionDryRunState.MovingToBolt:
                await _inspector.MoveToAsync(target.Bolt!, cancellationToken);
                return;
            case InspectionDryRunState.ReadingBarcode:
                LastBarcode = await _inspector.ReadBarcodeAsync(target.Pcb, cancellationToken);
                LastBoltPresent = null;
                break;
            case InspectionDryRunState.InspectingBolt:
                LastBoltPresent = await _inspector.InspectAsync(target.Bolt!, cancellationToken);
                break;
            default:
                await WaitForChangeAsync(cancellationToken);
                return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var index = Array.IndexOf(_targets, target);
        if (Direction == InspectionRouteDirection.Forward && index == _targets.Length - 1
            || Direction == InspectionRouteDirection.Return && index == 0)
        {
            CompletedPasses++;
            Direction = Direction == InspectionRouteDirection.Forward
                ? InspectionRouteDirection.Return : InspectionRouteDirection.Forward;
        }
        if (_targets.Length > 1)
            _current = _targets[index + (Direction == InspectionRouteDirection.Forward ? 1 : -1)];
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();
    private sealed record Target(HeatSinkSlot Pcb, BoltTarget? Bolt);
}
