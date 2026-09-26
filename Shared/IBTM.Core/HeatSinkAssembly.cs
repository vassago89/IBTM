using System;
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

    public long? PcbNumber { get; set; }

    public event Action<HeatSinkAssembly>? ResultsChanged;
    public event Action<InspectionCapture>? InspectionCaptured;

    public IReadOnlyDictionary<int, BoltResult> PcbBoltResults => _pcbBoltResults;

    public IReadOnlyDictionary<int, BoltResult> PickupBoltResults => _pickupBoltResults;

    public IReadOnlyDictionary<int, bool> BoltPresenceResults => _boltPresenceResults;

    public AssemblyResult FasteningResult { get; private set; }
    public AssemblyResult InspectionResult { get; private set; }
    // Pending is untested; assigning a null barcode records a failed read.
    public AssemblyResult PcbBarcodeResult { get; private set; }
    public string? PcbBarcode
    {
        get;
        set
        {
            field = value;
            PcbBarcodeResult = string.IsNullOrEmpty(value) ? AssemblyResult.Ng : AssemblyResult.Ok;
            if (PcbBarcodeResult == AssemblyResult.Ng)
                InspectionResult = AssemblyResult.Ng;
            ResultsChanged?.Invoke(this);
        }
    }

    public AssemblyResult Result => FasteningResult == AssemblyResult.Ng ? AssemblyResult.Ng : InspectionResult;

    public void RecordBolt(FasteningHead head, int number, BoltResult result)
    {
        var results = head switch
        {
            FasteningHead.Shooting => _pcbBoltResults,
            FasteningHead.Pickup => _pickupBoltResults,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        results[number] = result;
        if (!result.Success)
        {
            FasteningResult = AssemblyResult.Ng;
        }
        ResultsChanged?.Invoke(this);
    }

    public void CompleteFastening()
    {
        if (FasteningResult != AssemblyResult.Ng)
        {
            FasteningResult = AssemblyResult.Ok;
        }
        ResultsChanged?.Invoke(this);
    }

    public void RecordBoltPresence(int number, bool present)
    {
        _boltPresenceResults[number] = present;
        if (!present)
        {
            InspectionResult = AssemblyResult.Ng;
        }
        ResultsChanged?.Invoke(this);
    }

    public void CompleteInspection()
    {
        if (InspectionResult != AssemblyResult.Ng)
        {
            InspectionResult = AssemblyResult.Ok;
        }
        ResultsChanged?.Invoke(this);
    }

    public void RecordInspectionCapture(InspectionCapture capture)
    {
        InspectionCaptured?.Invoke(capture);
    }
}
