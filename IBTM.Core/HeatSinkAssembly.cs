using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

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
    private static readonly BoltResult ManualCompletion =
        new(true, 0, BoltResultSource.Manual);
    private readonly ConcurrentDictionary<int, BoltResult> _pcbBoltResults = new();
    private readonly ConcurrentDictionary<int, BoltResult> _ipmSeatingResults = new();
    private readonly ConcurrentDictionary<int, BoltResult> _ipmFinalResults = new();
    private readonly ConcurrentDictionary<int, bool> _boltPresenceResults = new();

    public HeatSinkSlot HeatSink { get; } = heatSink;
    public IReadOnlyDictionary<int, BoltResult> PcbBoltResults => _pcbBoltResults;
    public IReadOnlyDictionary<int, BoltResult> IpmSeatingResults => _ipmSeatingResults;
    public IReadOnlyDictionary<int, BoltResult> IpmFinalResults => _ipmFinalResults;
    public IReadOnlyDictionary<int, bool> BoltPresenceResults => _boltPresenceResults;
    public AssemblyResult FasteningResult { get; private set; }
    public AssemblyResult InspectionResult { get; private set; }

    public AssemblyResult Result =>
        FasteningResult == AssemblyResult.Ng
            ? AssemblyResult.Ng
            : InspectionResult;

    public void RecordPcbBolt(int number, BoltResult result) =>
        Record(_pcbBoltResults, number, result);

    public void RecordIpmSeating(int number, BoltResult result) =>
        Record(_ipmSeatingResults, number, result);

    public void RecordIpmFinal(int number, BoltResult result) =>
        Record(_ipmFinalResults, number, result);

    private void Record(
        ConcurrentDictionary<int, BoltResult> results,
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

    public void PrepareFasteningRecovery(
        IEnumerable<(int Number, bool Completed)> pcbBolts,
        IEnumerable<(int Number, bool Completed)> ipmSeatingBolts,
        IEnumerable<(int Number, bool Completed)> ipmFinalBolts)
    {
        ApplyCompletion(_pcbBoltResults, pcbBolts);
        ApplyCompletion(_ipmSeatingResults, ipmSeatingBolts);
        ApplyCompletion(_ipmFinalResults, ipmFinalBolts);
        FasteningResult = _pcbBoltResults.Values
            .Concat(_ipmSeatingResults.Values)
            .Concat(_ipmFinalResults.Values)
            .Any(result => !result.Success)
            ? AssemblyResult.Ng
            : AssemblyResult.Pending;
    }

    private static void ApplyCompletion(
        ConcurrentDictionary<int, BoltResult> results,
        IEnumerable<(int Number, bool Completed)> items)
    {
        foreach (var (number, completed) in items)
        {
            if (completed)
            {
                results.TryAdd(number, ManualCompletion);
            }
            else
            {
                results.TryRemove(number, out _);
            }
        }
    }

    public void RecordBoltPresence(int number, bool present)
    {
        _boltPresenceResults[number] = present;
        if (!present)
        {
            InspectionResult = AssemblyResult.Ng;
        }
    }

    public void ResetInspection()
    {
        _boltPresenceResults.Clear();
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
