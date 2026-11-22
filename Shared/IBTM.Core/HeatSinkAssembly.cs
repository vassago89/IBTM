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
    private readonly ConcurrentDictionary<Guid, BoltHeightResult> _heightResults;
    private string? _pcbBarcode;

    public HeatSinkAssembly(HeatSinkSlot heatSink)
    {
        _shootingBoltResults = new();
        _pickupBoltResults = new();
        _heightResults = new();
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
    public IReadOnlyDictionary<Guid, BoltHeightResult> HeightResults => _heightResults;
    public PcbAlignment? Alignment { get; private set; }
    public string? FiducialError { get; private set; }
    public bool IsFiducialNgExcluded { get; private set; }

    public void RecordAlignment(PcbAlignment alignment)
    {
        Alignment = alignment;
        FiducialError = null;
        IsFiducialNgExcluded = false;
        ResultsChanged?.Invoke(this);
    }

    public void RecordFiducialFailure(string error, bool excludeFromNg)
    {
        Alignment = null;
        FiducialError = error;
        IsFiducialNgExcluded = excludeFromNg;
        if (!excludeFromNg)
            InspectionResult = AssemblyResult.Ng;
        ResultsChanged?.Invoke(this);
    }

    public HeightReferencePlane? HeightReference { get; private set; }

    public void RecordHeightReference(HeightReferencePlane reference)
    {
        HeightReference = reference;
        ResultsChanged?.Invoke(this);
    }

    public void RecordHeight(Guid boltId, BoltHeightResult result)
    {
        _heightResults[boltId] = result;
        if (!result.Success)
            InspectionResult = AssemblyResult.Ng;
        ResultsChanged?.Invoke(this);
    }

    public AssemblyResult FasteningResult { get; private set; }
    public AssemblyResult InspectionResult { get; private set; }
    public int InspectionPass { get; private set; }
    // Pending is untested; assigning a null barcode records a failed read.
    public AssemblyResult PcbBarcodeResult { get; private set; }
    // Inspection policy applied to this read; later setting changes do not rewrite its verdict.
    public bool IsDataMatrixNgExcluded { get; set; }
    public string? PcbBarcode
    {
        get => _pcbBarcode;
        set
        {
            _pcbBarcode = value;
            PcbBarcodeResult = string.IsNullOrEmpty(value) ? AssemblyResult.Ng : AssemblyResult.Ok;
            if (PcbBarcodeResult == AssemblyResult.Ng && !IsDataMatrixNgExcluded)
                InspectionResult = AssemblyResult.Ng;
            ResultsChanged?.Invoke(this);
        }
    }

    public AssemblyResult Result => FasteningResult == AssemblyResult.Ng ? AssemblyResult.Ng : InspectionResult;

    public void RecordBolt(FasteningHead head, Guid boltId, BoltResult result)
    {
        GetBoltResults(head)[boltId] = result;
        if (IsFasteningNg(result))
            FasteningResult = AssemblyResult.Ng;
        ResultsChanged?.Invoke(this);
    }

    public void ChangeBoltResult(FasteningHead head, Guid boltId, BoltResult? result)
    {
        var results = GetBoltResults(head);
        if (result is null)
            results.TryRemove(boltId, out _);
        else
            results[boltId] = result;

        // Explicit operator edits reopen this PCB; automatic results keep their NG latch.
        FasteningResult = _shootingBoltResults.Values.Concat(_pickupBoltResults.Values).Any(IsFasteningNg)
            ? AssemblyResult.Ng : AssemblyResult.Pending;
        ResultsChanged?.Invoke(this);
    }

    private ConcurrentDictionary<Guid, BoltResult> GetBoltResults(FasteningHead head)
    {
        return head switch
        {
            FasteningHead.Shooting => _shootingBoltResults,
            FasteningHead.Pickup => _pickupBoltResults,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
    }

    private static bool IsFasteningNg(BoltResult result)
    {
        return result.Source != BoltResultSource.Interrupted
            && (!result.Success || result.Source == BoltResultSource.DryRun);
    }

    public void CompleteFastening()
    {
        if (FasteningResult != AssemblyResult.Ng)
        {
            FasteningResult = _shootingBoltResults.Values.Concat(_pickupBoltResults.Values)
                .Any(result => result.Source == BoltResultSource.Interrupted)
                ? AssemblyResult.Pending : AssemblyResult.Ok;
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
        ResultsChanged?.Invoke(this);
    }

    public void ClearInspectionResults()
    {
        InspectionPass++;
        _heightResults.Clear();
        HeightReference = null;
        Alignment = null;
        FiducialError = null;
        IsFiducialNgExcluded = false;
        _pcbBarcode = null;
        PcbBarcodeResult = AssemblyResult.Pending;
        IsDataMatrixNgExcluded = false;
        InspectionResult = AssemblyResult.Pending;
        ResultsChanged?.Invoke(this);
        InspectionCleared?.Invoke(this);
    }
}
