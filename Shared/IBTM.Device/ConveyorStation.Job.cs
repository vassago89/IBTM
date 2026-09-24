using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Device;

public sealed partial class ConveyorStation
{
    // Protect only result ownership changes, never device calls or notifications.
    private static readonly Lock s_jobGate;
    private volatile Job _job;

    static ConveyorStation()
    {
        s_jobGate = new();
    }

    public event Action<HeatSinkAssembly>? AssemblyCreated;

    public Job CurrentJob => _job;

    public bool Completed => CarrierPresent && _job.Completed;

    public IEnumerable<HeatSinkAssembly> Assemblies => _job.Assemblies.Values.ToArray();

    public bool IsReceiveAllowed => !CarrierPresent;

    public bool HasNg => Assemblies.Any(assembly => assembly.Result == AssemblyResult.Ng);

    public bool IsTransferAllowed => CarrierPresent && Completed;

    public HeatSinkAssembly GetAssembly(HeatSinkSlot heatSink)
    {
        return GetAssembly(CurrentJob, heatSink);
    }

    public HeatSinkAssembly GetAssembly(Job job, HeatSinkSlot heatSink)
    {
        HeatSinkAssembly assembly;
        lock (s_jobGate)
        {
            RequireCurrentJob(job);
            if (job.Assemblies.TryGetValue(heatSink, out var existing))
                return existing;
            assembly = new(heatSink);
            job.Assemblies[heatSink] = assembly;
        }
        AssemblyCreated?.Invoke(assembly);
        return assembly;
    }

    public void RequireCurrentJob(Job job)
    {
        if (!ReferenceEquals(_job, job))
            throw new InvalidOperationException(
                $"Carrier work changed from {job.Id} to {_job.Id}; the previous work cannot update this carrier.");
    }

    public void TransferAssembliesTo(ConveyorStation destination, Job job)
    {
        lock (s_jobGate)
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
        lock (s_jobGate)
        {
            RequireCurrentJob(job);
            if (job.Completed)
                return;
            job.Completed = true;
        }
        Changed?.Invoke();
    }

    public void Restart(Job job)
    {
        lock (s_jobGate)
        {
            RequireCurrentJob(job);
            job.Completed = false;
        }
        // Keep recorded quality results with the carrier; they do not select sequence steps.
        Changed?.Invoke();
    }

    public void StartRepeat(Job job)
    {
        lock (s_jobGate)
        {
            RequireCurrentJob(job);
            if (!CarrierPresent || !job.Completed)
                throw new InvalidOperationException("Finish the current carrier work before starting another stationary repeat.");
            _job = new();
        }
        Changed?.Invoke();
    }

    public sealed class Job
    {
        private static long s_nextId;
        internal readonly ConcurrentDictionary<HeatSinkSlot, HeatSinkAssembly> Assemblies;
        internal volatile bool Completed;

        internal Job(long? id = null)
        {
            Assemblies = new();

            Id = id ?? Interlocked.Increment(ref s_nextId);
        }

        public long Id { get; }
    }
}
