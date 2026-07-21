namespace IBTM.Core.Machine;

public sealed class MachineRuntimeSettings
{
    public int LiftSettleDelayMs { get; set; } = 500;
    public int AlignSettleDelayMs { get; set; } = 300;

    public void CopyFrom(MachineRuntimeSettings source)
    {
        LiftSettleDelayMs = source.LiftSettleDelayMs;
        AlignSettleDelayMs = source.AlignSettleDelayMs;
    }
}
