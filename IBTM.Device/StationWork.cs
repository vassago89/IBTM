using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Device;

public abstract class StationWork
{
    private readonly Func<bool> _isEnabled;
    private volatile ConcurrentDictionary<HeatSinkSlot, HeatSinkAssembly> _assemblies = new();
    private bool _completed;

    protected StationWork(ConveyorStation station, Func<bool>? isEnabled = null)
    {
        _isEnabled = isEnabled ?? AlwaysEnabled;
        Station = station;
        station.Changed += NotifyChanged;
        station.CarrierChanged += OnCarrierChanged;
    }

    public event Action? Changed;
    public bool Enabled
    {
        get
        {
            return _isEnabled();
        }
    }

    public ConveyorStation Station { get; }

    public bool CarrierPresent
    {
        get
        {
            return Station.CarrierPresent;
        }
    }

    public StationCylinderState BackupPlate
    {
        get
        {
            return Station.BackupPlate;
        }
    }

    public StationCylinderState Stopper
    {
        get
        {
            return Station.Stopper;
        }
    }

    public bool CarrierSeated
    {
        get
        {
            return CarrierPresent
                && BackupPlate == StationCylinderState.Up
                && Stopper == StationCylinderState.Down;
        }
    }

    public bool Completed
    {
        get
        {
            return !Enabled || Volatile.Read(ref _completed);
        }
    }

    public IEnumerable<HeatSinkAssembly> Assemblies
    {
        get
        {
            return _assemblies.Select(item => item.Value);
        }
    }

    public virtual bool CanReceive
    {
        get
        {
            return !CarrierPresent;
        }
    }

    public virtual bool HasNg
    {
        get
        {
            return Assemblies.Any(assembly => assembly.Result == AssemblyResult.Ng);
        }
    }

    public bool CanTransfer
    {
        get
        {
            return CarrierPresent && Completed && Stopper == StationCylinderState.Down;
        }
    }

    public bool HeatSinkPresent(HeatSinkSlot heatSink)
    {
        return Station.HeatSinkPresent(heatSink);
    }

    public HeatSinkAssembly Assembly(HeatSinkSlot heatSink)
    {
        return _assemblies.GetOrAdd(heatSink, static slot => new HeatSinkAssembly(slot));
    }

    protected void RemoveAssembly(HeatSinkSlot heatSink)
    {
        _assemblies.TryRemove(heatSink, out _);
    }

    public void TransferAssembliesTo(StationWork destination)
    {
        var assemblies = _assemblies;
        _assemblies = new();
        destination.SetAssemblies(assemblies.Select(item => item.Value));
        Changed?.Invoke();
    }

    protected void SetAssemblies(IEnumerable<HeatSinkAssembly> assemblies)
    {
        _assemblies = new(
            assemblies.Select(
                assembly => new KeyValuePair<HeatSinkSlot, HeatSinkAssembly>(assembly.HeatSink, assembly)));
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

    protected void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private static bool AlwaysEnabled()
    {
        return true;
    }

    private void OnCarrierChanged(bool present)
    {
        if (present)
        {
            Volatile.Write(ref _completed, false);
            _assemblies = new();
        }
    }
}
