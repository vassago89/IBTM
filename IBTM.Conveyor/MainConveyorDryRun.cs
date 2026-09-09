using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public enum MainConveyorDestination
{
    [Description("Load one carrier")] None,
    [Description("Front sensor")] Entry,
    [Description("Station 1")] Station1,
    [Description("Station 2")] Station2,
    [Description("Station 3")] Station3,
}

public enum MainConveyorDryRunState
{
    [Description("Raise and clear the working heads")] Unavailable,
    [Description("Load one carrier inside the machine")] WaitingForCarrier,
    [Description("Lowering plates and stoppers for return")] ClearingReturnPath,
    [Description("Returning to front sensor")] Returning,
    [Description("Preparing station")] Preparing,
    [Description("Moving to station")] Moving,
    [Description("Seating carrier")] Seating,
    [Description("Destination reached")] Arrived,
}

// One carrier: Station 1 -> 2 -> 3 -> front sensor, then forward again.
// PCB recovery uses the same states but finishes when Station 1 is seated.
// Destination is unfinished route intent, never proof that a carrier exists.
public sealed class MainConveyorDryRun : AutoUnit
{
    private readonly MainConveyor _conveyor;
    private readonly ConveyorStation[] _stations;
    private Route? _route;

    public MainConveyorDryRun(MainConveyor conveyor, ConveyorStation[] stations)
    {
        _conveyor = conveyor;
        _stations = stations;
        conveyor.Changed += NotifyChanged;
    }

    public override event Action? Changed;
    public MainConveyorDestination Destination { get; private set; }
    public int CompletedPasses { get; private set; }
    public bool ReturningToStation1 => _route == Route.ReturnToStation1 && !AtStation1;
    public bool AtStation1 => Destination == MainConveyorDestination.Station1
        && State == MainConveyorDryRunState.Arrived;
    private ConveyorStation Target => _stations[(int)Destination - (int)MainConveyorDestination.Station1];
    private ConveyorStation? Source => Destination > MainConveyorDestination.Station1
        ? _stations[(int)Destination - (int)MainConveyorDestination.Station1 - 1] : null;
    private static bool Released(ConveyorStation station) =>
        station.BackupPlate == StationCylinderState.Down && station.Stopper == StationCylinderState.Down;

    public MainConveyorDryRunState State => Destination switch
    {
        MainConveyorDestination.None => MainConveyorDryRunState.WaitingForCarrier,
        MainConveyorDestination.Entry => _conveyor.EntryCarrierDetected ? MainConveyorDryRunState.Arrived
            : _stations.All(Released) ? MainConveyorDryRunState.Returning : MainConveyorDryRunState.ClearingReturnPath,
        _ => Target.CarrierPresent
            ? Target.BackupPlate == StationCylinderState.Up && Target.Stopper == StationCylinderState.Down
                && (Source is null || Source.BackupPlate == StationCylinderState.Up)
                ? MainConveyorDryRunState.Arrived : MainConveyorDryRunState.Seating
            : Target.BackupPlate == StationCylinderState.Down && Target.Stopper == StationCylinderState.Up
                && (Source is null || Released(Source))
                ? MainConveyorDryRunState.Moving : MainConveyorDryRunState.Preparing,
    };

    public Task RunAsync(CancellationToken cancellationToken) =>
        RunAsync(Route.RoundTrip, cancellationToken);

    public Task ReturnToStation1Async(CancellationToken cancellationToken) =>
        RunAsync(Route.ReturnToStation1, cancellationToken);

    private async Task RunAsync(Route route, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = _stations.Count(station => station.CarrierPresent) + (_conveyor.EntryCarrierDetected ? 1 : 0);
        if (count > 1 || _conveyor.ExitCarrierDetected)
            throw new InvalidOperationException("Main conveyor dry run requires one carrier inside the machine and a clear exit.");
        var index = Array.FindIndex(_stations, station => station.CarrierPresent);
        if (_route != route || Destination == MainConveyorDestination.None
            || route == Route.ReturnToStation1 && index > 0)
        {
            if (count == 0 && (_route is null || route == Route.RoundTrip))
                throw new InvalidOperationException("Place one carrier at the front sensor or a station before dry run.");
            Destination = route == Route.ReturnToStation1
                ? index == 0 ? MainConveyorDestination.Station1 : MainConveyorDestination.Entry
                : index < 0 ? MainConveyorDestination.Station1
                : MainConveyorDestination.Station1 + index;
            _route = route;
        }
        NotifyChanged();
        await _conveyor.RunDryRunAsync(token => RunLoopAsync(ExecuteAsync, token,
            route == Route.ReturnToStation1 ? () => AtStation1 : null), cancellationToken);
        if (route == Route.ReturnToStation1 && AtStation1)
        {
            _route = null;
            NotifyChanged();
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        switch (State)
        {
            case MainConveyorDryRunState.ClearingReturnPath:
                await Task.WhenAll(_stations.Select(station => station.ReleaseAsync(cancellationToken)));
                break;
            case MainConveyorDryRunState.Returning:
                await _conveyor.RunUntilAsync(InputIo.MainConveyorEntryCarrierDetected, true, cancellationToken);
                break;
            case MainConveyorDryRunState.Preparing:
                await Task.WhenAll(Target.PrepareToReceiveAsync(cancellationToken),
                    Source?.ReleaseAsync(cancellationToken) ?? Task.CompletedTask);
                break;
            case MainConveyorDryRunState.Moving:
                await _conveyor.RunUntilAsync(Destination switch
                {
                    MainConveyorDestination.Station1 => InputIo.PcbPlacementCarrierPresent,
                    MainConveyorDestination.Station2 => InputIo.BoltFasteningCarrierPresent,
                    MainConveyorDestination.Station3 => InputIo.InspectionCarrierPresent,
                    _ => throw new InvalidOperationException(),
                }, false, cancellationToken);
                break;
            case MainConveyorDryRunState.Seating:
                await Task.WhenAll(Target.SeatAsync(cancellationToken),
                    Source?.RaiseBackupPlateAsync(cancellationToken) ?? Task.CompletedTask);
                break;
            case MainConveyorDryRunState.Arrived:
                if (Destination is MainConveyorDestination.Entry or MainConveyorDestination.Station3)
                    CompletedPasses++;
                Destination = Destination == MainConveyorDestination.Station3
                    ? MainConveyorDestination.Entry : Destination + 1;
                NotifyChanged();
                break;
            default:
                await WaitForChangeAsync(cancellationToken);
                break;
        }
    }

    private void NotifyChanged() => Changed?.Invoke();
    private enum Route { RoundTrip, ReturnToStation1 }
}
