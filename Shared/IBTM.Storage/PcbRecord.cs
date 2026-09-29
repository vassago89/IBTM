using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Storage;

public sealed record PcbRecord(
    [property: JsonIgnore] long Number,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RecipeName,
    HeatSinkSlot HeatSink,
    string? PcbBarcode,
    AssemblyResult PcbBarcodeResult,
    AssemblyResult FasteningResult,
    AssemblyResult InspectionResult,
    [property: JsonPropertyName("PcbBoltResults")] IReadOnlyDictionary<Guid, BoltResult> ShootingBoltResults,
    IReadOnlyDictionary<Guid, BoltResult> PickupBoltResults,
    IReadOnlyDictionary<Guid, bool> BoltPresenceResults,
    IReadOnlyList<Guid> BoltIds)
{
    public AssemblyResult? TurnsResult { get; init; }

    // Display metadata captured for this PCB. Results and images remain keyed by GUID.
    public IReadOnlyDictionary<Guid, string?>? BoltNames { get; init; }

    // The order belongs to this recorded PCB, independent of later recipe edits.
    public int? GetBoltOrdinal(Guid boltId)
    {
        for (var index = 0; index < BoltIds.Count; index++)
        {
            if (BoltIds[index] == boltId)
                return index + 1;
        }
        return null;
    }

    [JsonIgnore]
    public string? DatabaseFile { get; init; }

    [JsonIgnore]
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
}
