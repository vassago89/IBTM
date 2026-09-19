using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Device;

public abstract class StationWork
{
    // Protect only result ownership changes, never device calls or notifications.
    private static readonly Lock JobGate;
    private volatile Job _job;

    static StationWork()
    {
        JobGate = new();
    }

    protected StationWork(ConveyorStation station, UnitSettings units)
    {
        _job = new();

        Units = units;
        Station = station;
        station.Changed += NotifyChanged;
        station.CarrierChanged += OnCarrierChanged;
    }

    public event Action? Changed;

    public Job CurrentJob => _job;

    protected UnitSettings Units { get; }

    public abstract bool Enabled { get; }

    public ConveyorStation Station { get; }

    public virtual bool Completed
    {
        get
        {
            return Enabled
                ? Volatile.Read(ref _job.Completed)
                : Station.CarrierPresent && Station.BackupPlate == StationCylinderState.Up;
        }
    }

    public IEnumerable<HeatSinkAssembly> Assemblies => _job.Assemblies.Values.ToArray();

    public virtual bool IsReceiveAllowed => !Station.CarrierPresent;

    public virtual bool HasNg => Assemblies.Any(assembly => assembly.Result == AssemblyResult.Ng);

    public virtual bool IsTransferAllowed => Station.CarrierPresent && Completed && Station.Stopper == StationCylinderState.Down;

    public HeatSinkAssembly GetAssembly(HeatSinkSlot heatSink)
    {
        return GetAssembly(CurrentJob, heatSink);
    }

    public HeatSinkAssembly GetAssembly(Job job, HeatSinkSlot heatSink)
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

    private void OnCarrierChanged(bool present)
    {
        if (present)
        {
            lock (JobGate)
                _job = new();
        }
    }

    public sealed class Job
    {
        private static long _nextId;
        internal readonly ConcurrentDictionary<HeatSinkSlot, HeatSinkAssembly> Assemblies;
        internal bool Completed;

        internal Job(long? id = null)
        {
            Assemblies = new();

            Id = id ?? Interlocked.Increment(ref _nextId);
        }

        public long Id { get; }
    }
}
