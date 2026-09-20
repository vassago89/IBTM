using System;

namespace IBTM.Device;

public abstract partial class StationWork
{
    public void StartRepeat(Job job)
    {
        lock (s_jobGate)
        {
            RequireCurrentJob(job);
            if (!Station.CarrierPresent || !job.Completed)
                throw new InvalidOperationException("Finish the current carrier work before starting another stationary repeat.");
            _job = new();
        }
        Changed?.Invoke();
    }
}
