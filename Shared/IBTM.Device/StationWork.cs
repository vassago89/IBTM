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
    // Protect only result ownership changes, never device calls or notifications.
    private static readonly Lock JobGate = new();
    private volatile Job _job = new();

    public sealed class Job
    {
        private static long _nextId;
        internal readonly ConcurrentDictionary<HeatSinkSlot, HeatSinkAssembly> Assemblies = new();
        internal bool Completed;

        internal Job(long? id = null)
        {
            Id = id ?? Interlocked.Increment(ref _nextId);
        }

        public long Id { get; }
    }

    public Job CurrentJob
    {
        get
        {
            return _job;
        }
    }

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

    public virtual bool Completed
    {
        get
        {
            return Enabled
                ? Volatile.Read(ref _job.Completed)
                : CarrierPresent && BackupPlate == StationCylinderState.Up;
        }
    }

    public IEnumerable<HeatSinkAssembly> Assemblies
    {
        get
        {
            return _job.Assemblies.Values.ToArray();
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

    public virtual bool CanTransfer
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
        return Assembly(CurrentJob, heatSink);
    }

    public HeatSinkAssembly Assembly(Job job, HeatSinkSlot heatSink)
    {
        lock (JobGate)
        {
            RequireCurrentJob(job);
            return job.Assemblies.GetOrAdd(heatSink, static slot => new HeatSinkAssembly(slot));
        }
    }

    public void RequireCurrentJob(Job job)
    {
        if (!ReferenceEquals(_job, job))
            throw new InvalidOperationException(
                $"Carrier work changed from {job.Id} to {_job.Id}; the previous work cannot update this carrier.");
    }

    public void TransferAssembliesTo(StationWork destination, Job job)
    {
        lock (JobGate)
        {
            // The carrier keeps its trace number; each station gets a new completion owner.
            var received = new Job(job.Id);
            foreach (var assembly in job.Assemblies.Values)
                received.Assemblies[assembly.HeatSink] = assembly;
            destination._job = received;
            // A new carrier may already occupy the source. Only release the load
            // captured when this physical transfer started, never the new load.
            if (ReferenceEquals(_job, job))
                _job = new();
        }

        destination.Changed?.Invoke();
        Changed?.Invoke();
    }

    public void Complete(Job job)
    {
        lock (JobGate)
        {
            RequireCurrentJob(job);
            if (!Enabled || job.Completed)
                return;
            Volatile.Write(ref job.Completed, true);
        }
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
            lock (JobGate)
                _job = new();
        }
    }
}
