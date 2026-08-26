using System.Collections.Generic;
using System.ComponentModel;

namespace IBTM.Core;

public enum PcbResult
{
    [Description("Pending")]
    Pending,

    [Description("OK")]
    Ok,

    [Description("NG")]
    Ng,
}

public sealed class PcbAssembly(HousingSlot housing)
{
    public HousingSlot Housing { get; } = housing;
    public Dictionary<int, BoltResult> PcbBoltResults { get; } = [];
    public Dictionary<int, BoltResult> IpmSeatingResults { get; } = [];
    public Dictionary<int, BoltResult> IpmFinalResults { get; } = [];
    public Dictionary<int, bool> BoltPresenceResults { get; } = [];
    public PcbResult FasteningResult { get; private set; }
    public PcbResult InspectionResult { get; private set; }

    public PcbResult Result =>
        FasteningResult == PcbResult.Ng
        || InspectionResult == PcbResult.Ng
            ? PcbResult.Ng
            : InspectionResult == PcbResult.Ok
                ? PcbResult.Ok
                : PcbResult.Pending;

    public void BeginFastening()
    {
        PcbBoltResults.Clear();
        IpmSeatingResults.Clear();
        IpmFinalResults.Clear();
        FasteningResult = PcbResult.Pending;
    }

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
            FasteningResult = PcbResult.Ng;
        }
    }

    public void CompleteFastening()
    {
        if (FasteningResult != PcbResult.Ng)
        {
            FasteningResult = PcbResult.Ok;
        }
    }

    public void BeginInspection()
    {
        BoltPresenceResults.Clear();
        InspectionResult = PcbResult.Pending;
    }

    public void RecordBoltPresence(int number, bool present)
    {
        BoltPresenceResults[number] = present;
        if (!present)
        {
            InspectionResult = PcbResult.Ng;
        }
    }

    public void CompleteInspection()
    {
        if (InspectionResult != PcbResult.Ng)
        {
            InspectionResult = PcbResult.Ok;
        }
    }
}
