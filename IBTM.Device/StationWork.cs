using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Device;

public abstract class StationWork
{
    private volatile ConcurrentDictionary<HeatSinkSlot, HeatSinkAssembly> _assemblies = new();
    private bool _completed;

    protected StationWork(ConveyorStation station, bool enabled)
    {
        Enabled = enabled;
        Station = station;
        station.Changed += NotifyChanged;
        station.CarrierChanged += OnCarrierChanged;
    }

    public event Action? Changed;
    protected bool Enabled { get; }
    public ConveyorStation Station { get; }
    public bool CarrierPresent => Station.CarrierPresent;
    public StationCylinderState BackupPlate => Station.BackupPlate;
    public StationCylinderState Stopper => Station.Stopper;
    public bool CarrierSeated =>
        CarrierPresent
        && BackupPlate == StationCylinderState.Up
        && Stopper == StationCylinderState.Down;
    public bool Completed => !Enabled || Volatile.Read(ref _completed);
    public IEnumerable<HeatSinkAssembly> Assemblies => _assemblies.Select(item => item.Value);
    public virtual bool CanReceive => !CarrierPresent;
    public virtual bool HasNg =>
        Assemblies.Any(assembly => assembly.Result == AssemblyResult.Ng);
    public bool CanTransfer =>
        CarrierPresent
        && Completed
        && Stopper == StationCylinderState.Down;

    public bool HeatSinkPresent(HeatSinkSlot heatSink) =>
        Station.HeatSinkPresent(heatSink);

    public HeatSinkAssembly Assembly(HeatSinkSlot heatSink) =>
        _assemblies.GetOrAdd(heatSink, static slot => new HeatSinkAssembly(slot));

    protected void RemoveAssembly(HeatSinkSlot heatSink) =>
        _assemblies.TryRemove(heatSink, out _);

    public void TransferAssembliesTo(StationWork destination)
    {
        var assemblies = _assemblies;
        _assemblies = new();
        destination.SetAssemblies(assemblies.Select(item => item.Value));
        Changed?.Invoke();
    }

    protected void SetAssemblies(IEnumerable<HeatSinkAssembly> assemblies)
    {
        _assemblies = new(assemblies.Select(assembly =>
            new KeyValuePair<HeatSinkSlot, HeatSinkAssembly>(assembly.HeatSink, assembly)));
        Volatile.Write(ref _completed, false);
        Changed?.Invoke();
    }

    public void Complete()
    {
        if (Completed)
        {
            return;
        }

        Volatile.Write(ref _completed, true);
        Changed?.Invoke();
    }

    protected void Restart()
    {
        Volatile.Write(ref _completed, false);
        Changed?.Invoke();
    }

    protected void NotifyChanged() => Changed?.Invoke();

    private void OnCarrierChanged(bool present)
    {
        if (present)
        {
            Volatile.Write(ref _completed, false);
            _assemblies = new();
        }
    }
}
