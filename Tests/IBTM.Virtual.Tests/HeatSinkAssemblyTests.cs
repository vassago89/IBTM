using System;
using IBTM.Core;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class HeatSinkAssemblyTests
{
    [Theory]
    [InlineData(3599, 10d, null, true, true, AssemblyResult.Ng, AssemblyResult.Ng)]
    [InlineData(3600, 10d, null, true, true, AssemblyResult.Ok, AssemblyResult.Ok)]
    [InlineData(3601, 10d, null, true, false, AssemblyResult.Ok, AssemblyResult.Ng)]
    [InlineData(3600, 10d, null, false, true, AssemblyResult.Ok, AssemblyResult.Ng)]
    [InlineData(3600, null, 10d, true, true, AssemblyResult.Ok, AssemblyResult.Ok)]
    [InlineData(3601, null, 10d, true, true, AssemblyResult.Ng, AssemblyResult.Ng)]
    [InlineData(3240, 8d, 10d, true, true, AssemblyResult.Ok, AssemblyResult.Ok)]
    [InlineData(2879, 8d, 10d, true, true, AssemblyResult.Ng, AssemblyResult.Ng)]
    public void TurnsLimitsAreIndependentOfControllerAndVisionResults(
        double angle, double? minimum, double? maximum,
        bool controllerOk, bool visionOk, AssemblyResult turnsResult, AssemblyResult finalResult)
    {
        var id = Guid.NewGuid();
        var result = new BoltResult(controllerOk, 8)
        {
            MinimumTurns = minimum,
            MaximumTurns = maximum,
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
        var maximumOnly = measured with { MaximumTurns = 12, Controller = null };
        Assert.Equal(AssemblyResult.Pending, maximumOnly.TurnsResult);
        var dryRun = measured with { MinimumTurns = 10, Source = BoltResultSource.DryRun };
        Assert.Null(dryRun.TotalTurns);
        Assert.Equal(AssemblyResult.Pending, dryRun.TurnsResult);

        var assembly = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Pickup, Guid.NewGuid(), maximumOnly);
        assembly.CompleteFastening();
        assembly.CompleteInspection();
        Assert.Equal(AssemblyResult.Pending, assembly.TurnsResult);
        Assert.Equal(AssemblyResult.Pending, assembly.Result);
    }
}
