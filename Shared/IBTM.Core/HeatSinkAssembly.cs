using System;
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

public sealed class HeatSinkAssembly
{
    private readonly ConcurrentDictionary<Guid, BoltResult> _pcbBoltResults;
    private readonly ConcurrentDictionary<Guid, BoltResult> _pickupBoltResults;
    private readonly ConcurrentDictionary<Guid, bool> _boltPresenceResults;

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

    public IReadOnlyDictionary<Guid, BoltResult> PcbBoltResults => _pcbBoltResults;

    public IReadOnlyDictionary<Guid, BoltResult> PickupBoltResults => _pickupBoltResults;

    public IReadOnlyDictionary<Guid, bool> BoltPresenceResults => _boltPresenceResults;

    public AssemblyResult FasteningResult { get; private set; }
    public AssemblyResult InspectionResult { get; private set; }
    public AssemblyResult? TurnsResult { get; private set; }
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

    public AssemblyResult Result => FasteningResult == AssemblyResult.Ng || TurnsResult == AssemblyResult.Ng
        ? AssemblyResult.Ng
        : TurnsResult == AssemblyResult.Pending && InspectionResult == AssemblyResult.Ok
            ? AssemblyResult.Pending : InspectionResult;

    public void RecordBolt(FasteningHead head, Guid boltId, BoltResult result)
    {
        var results = head switch
        {
            FasteningHead.Shooting => _pcbBoltResults,
            FasteningHead.Pickup => _pickupBoltResults,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        results[boltId] = result;
        if (!result.Success)
        {
            FasteningResult = AssemblyResult.Ng;
        }
        if (result.TurnsResult == AssemblyResult.Ng)
            TurnsResult = AssemblyResult.Ng;
        else if (result.MinimumTurns.HasValue && TurnsResult != AssemblyResult.Ng)
            TurnsResult = AssemblyResult.Pending;
        ResultsChanged?.Invoke(this);
    }

    public void CompleteFastening()
    {
        if (FasteningResult != AssemblyResult.Ng)
        {
            FasteningResult = AssemblyResult.Ok;
        }
        if (TurnsResult != AssemblyResult.Ng)
        {
            var checkedResults = _pcbBoltResults.Values.Concat(_pickupBoltResults.Values)
                .Where(result => result.MinimumTurns.HasValue).ToArray();
            TurnsResult = checkedResults.Length == 0 ? null
                : checkedResults.All(result => result.TurnsResult == AssemblyResult.Ok)
                    ? AssemblyResult.Ok : AssemblyResult.Pending;
        }
        ResultsChanged?.Invoke(this);
    }

    public void RecordBoltPresence(Guid boltId, bool present)
    {
        _boltPresenceResults[boltId] = present;
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
