using System.Collections.Generic;
using System.ComponentModel;

namespace IBTM.Core;

public enum AssemblyResult
{
    [Description("Pending")]
    Pending,

    [Description("OK")]
    Ok,

    [Description("NG")]
    Ng,
}

public sealed class HeatSinkAssembly(HeatSinkSlot heatSink)
{
    public HeatSinkSlot HeatSink { get; } = heatSink;
    public Dictionary<int, BoltResult> PcbBoltResults { get; } = [];
    public Dictionary<int, BoltResult> IpmSeatingResults { get; } = [];
    public Dictionary<int, BoltResult> IpmFinalResults { get; } = [];
    public Dictionary<int, bool> BoltPresenceResults { get; } = [];
    public AssemblyResult FasteningResult { get; private set; }
    public AssemblyResult InspectionResult { get; private set; }

    public AssemblyResult Result =>
        (FasteningResult, InspectionResult) switch
        {
            (AssemblyResult.Ng, _) or (_, AssemblyResult.Ng) => AssemblyResult.Ng,
            (_, AssemblyResult.Ok) => AssemblyResult.Ok,
            _ => AssemblyResult.Pending,
        };

    public void RecordPcbBolt(int number, BoltResult result) =>
        Record(PcbBoltResults, number, result);

    public void RecordIpmSeating(int number, BoltResult result) =>
        Record(IpmSeatingResults, number, result);

    public void RecordIpmFinal(int number, BoltResult result) =>
        Record(IpmFinalResults, number, result);

    private void Record(
        Dictionary<int, BoltResult> results,
        int number,
        BoltResult result)
    {
        results[number] = result;
        if (!result.Success)
        {
            FasteningResult = AssemblyResult.Ng;
        }
    }

    public void CompleteFastening()
    {
        if (FasteningResult != AssemblyResult.Ng)
        {
            FasteningResult = AssemblyResult.Ok;
        }
    }

    public void ResetFastening()
    {
        PcbBoltResults.Clear();
        IpmSeatingResults.Clear();
        IpmFinalResults.Clear();
        FasteningResult = AssemblyResult.Pending;
    }

    public void RecordBoltPresence(int number, bool present)
    {
        BoltPresenceResults[number] = present;
        if (!present)
        {
            InspectionResult = AssemblyResult.Ng;
        }
    }

    public void ResetInspection()
    {
        BoltPresenceResults.Clear();
        InspectionResult = AssemblyResult.Pending;
    }

    public void CompleteInspection()
    {
        if (InspectionResult != AssemblyResult.Ng)
        {
            InspectionResult = AssemblyResult.Ok;
        }
    }
}
