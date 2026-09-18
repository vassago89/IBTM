using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<int, BoltResult> _pcbBoltResults = new();
    private readonly ConcurrentDictionary<int, BoltResult> _ipmSeatingResults = new();
    private readonly ConcurrentDictionary<int, BoltResult> _ipmFinalResults = new();
    private readonly ConcurrentDictionary<int, bool> _boltPresenceResults = new();

    public HeatSinkSlot HeatSink { get; } = heatSink;

    public IReadOnlyDictionary<int, BoltResult> PcbBoltResults
    {
        get
        {
            return _pcbBoltResults;
        }
    }

    public IReadOnlyDictionary<int, BoltResult> IpmSeatingResults
    {
        get
        {
            return _ipmSeatingResults;
        }
    }

    public IReadOnlyDictionary<int, BoltResult> IpmFinalResults
    {
        get
        {
            return _ipmFinalResults;
        }
    }

    public IReadOnlyDictionary<int, bool> BoltPresenceResults
    {
        get
        {
            return _boltPresenceResults;
        }
    }

    public AssemblyResult FasteningResult { get; private set; }
    public AssemblyResult InspectionResult { get; private set; }
    public string? PcbBarcode { get; private set; }

    public void RecordBarcode(string barcode)
    {
        PcbBarcode = barcode;
    }

    public AssemblyResult Result
    {
        get
        {
            return FasteningResult == AssemblyResult.Ng ? AssemblyResult.Ng : InspectionResult;
        }
    }

    public void RecordPcbBolt(int number, BoltResult result)
    {
        Record(_pcbBoltResults, number, result);
    }

    public void RecordIpmSeating(int number, BoltResult result)
    {
        Record(_ipmSeatingResults, number, result);
    }

    public void RecordIpmFinal(int number, BoltResult result)
    {
        Record(_ipmFinalResults, number, result);
    }

    private void Record(ConcurrentDictionary<int, BoltResult> results, int number, BoltResult result)
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

    public void RecordBoltPresence(int number, bool present)
    {
        _boltPresenceResults[number] = present;
        if (!present)
        {
            InspectionResult = AssemblyResult.Ng;
        }
    }

    public void CompleteInspection()
    {
        if (InspectionResult != AssemblyResult.Ng)
        {
            InspectionResult = AssemblyResult.Ok;
        }
    }
}
