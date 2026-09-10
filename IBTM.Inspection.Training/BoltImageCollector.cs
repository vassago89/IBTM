using System;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;

namespace IBTM.Inspection.Training;

public sealed record BoltImageInspection(
    string RecipeName,
    int BoltNumber,
    HeatSinkSlot HeatSink,
    int RegionSize,
    AssemblyResult Result,
    DateTimeOffset CapturedAt);

public sealed partial class BoltImageCollector(
    BoltTrainingStore store,
    BoltTrainingSettings settings,
    Func<string> recipeName) : ObservableObject
{
    [ObservableProperty]
    private string? _error;

    public void ClearError()
    {
        Error = null;
    }

    public void Collect(BoltInspectionImage inspection)
    {
        if (Error is not null
            || settings.ImageCollection == InspectionImageCollection.Off
            || settings.ImageCollection == InspectionImageCollection.NgOnly
            && inspection.Present)
            return;

        var source = new BoltImageInspection(
            recipeName(),
            inspection.BoltNumber,
            inspection.HeatSink,
            inspection.RegionSize,
            inspection.Present ? AssemblyResult.Ok : AssemblyResult.Ng,
            inspection.CapturedAt);
        try
        {
            store.AddImage(
                $"{source.RecipeName} · {source.HeatSink.GetDescription()} · Bolt {source.BoltNumber}",
                inspection.Image,
                inspection.RegionSize,
                source);
        }
        catch (Exception exception)
        {
            // Collection is optional. Keep the inspection result and stop further writes until acknowledged.
            Error = $"Image collection stopped: {exception.GetBaseException().Message}";
        }
    }
}
