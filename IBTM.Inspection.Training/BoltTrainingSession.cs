using System;

namespace IBTM.Inspection.Training;

public sealed class BoltTrainingSession
{
    public event Action? Changed;

    public bool IsRunning { get; private set; }

    internal void SetRunning(bool value)
    {
        IsRunning = value;
        Changed?.Invoke();
    }
}
