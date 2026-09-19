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

public sealed class HeatSinkAssembly
{
    private readonly ConcurrentDictionary<int, BoltResult> _pcbBoltResults;
    private readonly ConcurrentDictionary<int, BoltResult> _pickupBoltResults;
    private readonly ConcurrentDictionary<int, bool> _boltPresenceResults;

    public HeatSinkAssembly(HeatSinkSlot heatSink)
    {
        _pcbBoltResults = new();
        _pickupBoltResults = new();
        _boltPresenceResults = new();
        HeatSink = heatSink;
    }

    public HeatSinkSlot HeatSink { get; }

    public IReadOnlyDictionary<int, BoltResult> PcbBoltResults => _pcbBoltResults;

    public IReadOnlyDictionary<int, BoltResult> PickupBoltResults => _pickupBoltResults;

    public IReadOnlyDictionary<int, bool> BoltPresenceResults => _boltPresenceResults;

    public AssemblyResult FasteningResult { get; private set; }
    public AssemblyResult InspectionResult { get; private set; }
    public string? PcbBarcode { get; set; }

    public AssemblyResult Result => FasteningResult == AssemblyResult.Ng ? AssemblyResult.Ng : InspectionResult;

    public void RecordPcbBolt(int number, BoltResult result)
    {
        Record(_pcbBoltResults, number, result);
    }

    public void RecordPickupBolt(int number, BoltResult result)
    {
        Record(_pickupBoltResults, number, result);
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
