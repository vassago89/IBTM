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
    IReadOnlyDictionary<int, BoltResult> PcbBoltResults,
    IReadOnlyDictionary<int, BoltResult> PickupBoltResults,
    IReadOnlyDictionary<int, bool> BoltPresenceResults)
{
    [JsonIgnore]
    public string? DatabaseFile { get; init; }

    [JsonIgnore]
    public AssemblyResult Result => FasteningResult == AssemblyResult.Ng
        ? AssemblyResult.Ng : InspectionResult;
}
