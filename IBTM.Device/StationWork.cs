using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Device;

public abstract class StationWork
{
    private readonly List<HeatSinkAssembly> _assemblies = [];
    private bool _completed;

    protected StationWork(ConveyorStation station)
    {
        Station = station;
        station.Changed += OnStationChanged;
        station.CarrierChanged += OnCarrierChanged;
        station.HeatSinkChanged += OnHeatSinkChanged;
    }

    public event Action? Changed;
    public ConveyorStation Station { get; }
    public bool CarrierPresent => Station.CarrierPresent;
    public bool HeatSink1Present => Station.HeatSink1Present;
    public bool HeatSink2Present => Station.HeatSink2Present;
    public StationCylinderState BackupPlate => Station.BackupPlate;
    public StationCylinderState Stopper => Station.Stopper;
    public bool CarrierSeated => Station.Seated;
    public bool Completed => Volatile.Read(ref _completed);
    public IReadOnlyList<HeatSinkAssembly> Assemblies => _assemblies;
    public virtual bool CanReceive => Station.CanReceive;
    public virtual bool HasNg =>
        _assemblies.Any(assembly =>
            HeatSinkPresent(assembly.HeatSink)
            && assembly.Result == AssemblyResult.Ng);
    public bool CanTransfer =>
        CarrierPresent
        && Completed
        && Stopper == StationCylinderState.Down;

    public bool HeatSinkPresent(HeatSinkSlot heatSink) =>
        Station.HeatSinkPresent(heatSink);

    public HeatSinkAssembly Assembly(HeatSinkSlot heatSink)
    {
        var assembly = _assemblies.FirstOrDefault(
            item => item.HeatSink == heatSink);
        if (assembly is not null)
        {
            return assembly;
        }

        assembly = new HeatSinkAssembly(heatSink);
        _assemblies.Add(assembly);
        return assembly;
    }

    public void TransferAssembliesTo(StationWork destination)
    {
        destination.SetAssemblies(_assemblies);
        _assemblies.Clear();
        Changed?.Invoke();
    }

    protected void SetAssemblies(IEnumerable<HeatSinkAssembly> assemblies)
    {
        _assemblies.Clear();
        _assemblies.AddRange(assemblies);
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

    private void OnStationChanged()
    {
        Changed?.Invoke();
    }

    private void OnCarrierChanged(bool present)
    {
        if (present)
        {
            Volatile.Write(ref _completed, false);
            _assemblies.Clear();
        }
    }

    private void OnHeatSinkChanged()
    {
        Volatile.Write(ref _completed, false);
    }
}
