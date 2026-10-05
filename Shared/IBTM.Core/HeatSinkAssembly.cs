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
    private readonly ConcurrentDictionary<Guid, BoltResult> _shootingBoltResults;
    private readonly ConcurrentDictionary<Guid, BoltResult> _pickupBoltResults;
    private readonly ConcurrentDictionary<Guid, bool> _boltPresenceResults;
    private string? _pcbBarcode;

    public HeatSinkAssembly(HeatSinkSlot heatSink)
    {
        _shootingBoltResults = new();
        _pickupBoltResults = new();
        _boltPresenceResults = new();
        HeatSink = heatSink;
    }

    public HeatSinkSlot HeatSink { get; }

    public long? PcbNumber { get; set; }

    // Completion history for this carrier, not a live PCB presence signal.
    public bool IsPlacementCompleted { get; set; }

    public event Action<HeatSinkAssembly>? ResultsChanged;
    public event Action<InspectionCapture>? InspectionCaptured;
    public event Action<HeatSinkAssembly>? InspectionCleared;

    public IReadOnlyDictionary<Guid, BoltResult> ShootingBoltResults => _shootingBoltResults;

    public IReadOnlyDictionary<Guid, BoltResult> PickupBoltResults => _pickupBoltResults;

    public IReadOnlyDictionary<Guid, bool> BoltPresenceResults => _boltPresenceResults;

    public AssemblyResult FasteningResult { get; private set; }
    public AssemblyResult InspectionResult { get; private set; }
    public AssemblyResult? TurnsResult { get; private set; }
    // Pending is untested; assigning a null barcode records a failed read.
    public AssemblyResult PcbBarcodeResult { get; private set; }
    public string? PcbBarcode
    {
        get => _pcbBarcode;
        set
        {
            _pcbBarcode = value;
            PcbBarcodeResult = string.IsNullOrEmpty(value) ? AssemblyResult.Ng : AssemblyResult.Ok;
            if (PcbBarcodeResult == AssemblyResult.Ng)
                InspectionResult = AssemblyResult.Ng;
            ResultsChanged?.Invoke(this);
        }
    }

    public AssemblyResult Result
    {
        get
        {
            if (FasteningResult == AssemblyResult.Ng || TurnsResult == AssemblyResult.Ng)
                return AssemblyResult.Ng;
            if (TurnsResult == AssemblyResult.Pending && InspectionResult == AssemblyResult.Ok)
                return AssemblyResult.Pending;
            return InspectionResult;
        }
    }

    public void RecordBolt(FasteningHead head, Guid boltId, BoltResult result)
    {
        var results = head switch
        {
            FasteningHead.Shooting => _shootingBoltResults,
            FasteningHead.Pickup => _pickupBoltResults,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        results[boltId] = result;
        if (!result.Success || result.Source == BoltResultSource.DryRun)
        {
            FasteningResult = AssemblyResult.Ng;
        }
        if (result.TurnsResult == AssemblyResult.Ng)
            TurnsResult = AssemblyResult.Ng;
        else if (result.TurnsResult.HasValue && TurnsResult != AssemblyResult.Ng)
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
            var checkedResults = _shootingBoltResults.Values.Concat(_pickupBoltResults.Values)
                .Where(result => result.TurnsResult.HasValue).ToArray();
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

    public void ClearFasteningResults()
    {
        _shootingBoltResults.Clear();
        _pickupBoltResults.Clear();
        FasteningResult = AssemblyResult.Pending;
        TurnsResult = null;
        ResultsChanged?.Invoke(this);
    }

    public void ClearInspectionResults()
    {
        _boltPresenceResults.Clear();
        _pcbBarcode = null;
        PcbBarcodeResult = AssemblyResult.Pending;
        InspectionResult = AssemblyResult.Pending;
        ResultsChanged?.Invoke(this);
        InspectionCleared?.Invoke(this);
    }
}
