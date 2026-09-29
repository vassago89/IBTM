using System;
using IBTM.Core;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class HeatSinkAssemblyTests
{
    [Fact]
    public void EmptyBoltGuidCannotChangeResultsOrPublishAnImage()
    {
        var assembly = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        var resultEvents = 0;
        InspectionCapture? published = null;
        assembly.ResultsChanged += _ => resultEvents++;
        assembly.InspectionCaptured += capture => published = capture;
        var image = new InspectionCapture(Guid.Empty, DateTimeOffset.Now,
            new ImageFrame(1, 1, 3, new byte[3]), new(0, 0, 1, 1), false);

        Assert.Throws<ArgumentException>(() => assembly.RecordBolt(FasteningHead.Shooting, Guid.Empty, new(false, null)));
        Assert.Throws<ArgumentException>(() => assembly.RecordBoltPresence(Guid.Empty, false));
        Assert.Throws<ArgumentException>(() => assembly.RecordInspectionCapture(image));

        Assert.Empty(assembly.ShootingBoltResults);
        Assert.Empty(assembly.BoltPresenceResults);
        Assert.Equal(AssemblyResult.Pending, assembly.FasteningResult);
        Assert.Equal(AssemblyResult.Pending, assembly.InspectionResult);
        Assert.Equal(0, resultEvents);
        Assert.Null(published);

        var dataMatrix = image with { BoltId = null, Barcode = "PCB-123", Success = true };
        assembly.RecordInspectionCapture(dataMatrix);
        Assert.Same(dataMatrix, published);
        var boltImage = image with { BoltId = Guid.NewGuid() };
        assembly.RecordInspectionCapture(boltImage);
        Assert.Same(boltImage, published);
    }

    [Theory]
    [InlineData(3599, true, true, AssemblyResult.Ng, AssemblyResult.Ng)]
    [InlineData(3600, true, true, AssemblyResult.Ok, AssemblyResult.Ok)]
    [InlineData(3601, true, false, AssemblyResult.Ok, AssemblyResult.Ng)]
    [InlineData(3600, false, true, AssemblyResult.Ok, AssemblyResult.Ng)]
    public void MinimumTurnsIsIndependentOfControllerAndVisionResults(
        double angle, bool controllerOk, bool visionOk, AssemblyResult turnsResult, AssemblyResult finalResult)
    {
        var id = Guid.NewGuid();
        var result = new BoltResult(controllerOk, 8)
        {
            MinimumTurns = 10,
            Controller = new("COM9", 1, 1, 1000, 1, 8, 800, 100, 200, angle, 1, 0, 0, 1, 0, null),
        };
        var assembly = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Pickup, id, result);
        assembly.CompleteFastening();
        assembly.RecordBoltPresence(id, visionOk);
        assembly.CompleteInspection();

        Assert.Equal(angle / 360, result.TotalTurns);
        Assert.Equal(turnsResult, result.TurnsResult);
        Assert.Equal(turnsResult, assembly.TurnsResult);
        Assert.Equal(controllerOk ? AssemblyResult.Ok : AssemblyResult.Ng, assembly.FasteningResult);
        Assert.Equal(visionOk ? AssemblyResult.Ok : AssemblyResult.Ng, assembly.InspectionResult);
        Assert.Equal(finalResult, assembly.Result);
    }

    [Fact]
    public void MissingMeasurementAndDisabledTurnsCheckAreNotMeasuredOk()
    {
        var measured = new BoltResult(true, 8)
        {
            Controller = new("COM9", 1, 1, 1000, 1, 8, 800, 100, 200, 3600, 1, 0, 0, 1, 0, null),
        };
        Assert.Null(measured.TurnsResult);
        var missing = measured with { MinimumTurns = 10, Controller = null };
        Assert.Null(missing.TotalTurns);
        Assert.Equal(AssemblyResult.Pending, missing.TurnsResult);
        var dryRun = measured with { MinimumTurns = 10, Source = BoltResultSource.DryRun };
        Assert.Null(dryRun.TotalTurns);
        Assert.Equal(AssemblyResult.Pending, dryRun.TurnsResult);

        var assembly = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Pickup, Guid.NewGuid(), missing);
        assembly.CompleteFastening();
        assembly.CompleteInspection();
        Assert.Equal(AssemblyResult.Pending, assembly.TurnsResult);
        Assert.Equal(AssemblyResult.Pending, assembly.Result);
    }
}
