using System;
using IBTM.Core;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class HeatSinkAssemblyTests
{
    [Fact]
    public void FiducialExclusionKeepsBarcodeFasteningAndHeightNg()
    {
        var barcode = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        barcode.PcbBarcode = null;
        barcode.RecordFiducialFailure("Not found", excludeFromNg: true);
        barcode.CompleteInspection();
        Assert.Equal(AssemblyResult.Ng, barcode.PcbBarcodeResult);
        Assert.Equal(AssemblyResult.Ng, barcode.Result);

        var fastening = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        fastening.RecordBolt(FasteningHead.Pickup, Guid.NewGuid(), new(false, 8));
        fastening.RecordFiducialFailure("Not found", excludeFromNg: true);
        fastening.CompleteFastening();
        fastening.CompleteInspection();
        Assert.Equal(AssemblyResult.Ok, fastening.InspectionResult);
        Assert.Equal(AssemblyResult.Ng, fastening.Result);

        var height = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        height.RecordFiducialFailure("Not found", excludeFromNg: true);
        height.RecordBolt(FasteningHead.Pickup, Guid.NewGuid(), new(true, 8));
        height.RecordHeight(Guid.NewGuid(), new(DateTimeOffset.Now, new(0, 0, 2), 0, 0, 1));
        height.CompleteFastening();
        height.CompleteInspection();
        Assert.Equal(AssemblyResult.Ok, height.FasteningResult);
        Assert.Equal(AssemblyResult.Ng, height.InspectionResult);
        Assert.Equal(AssemblyResult.Ng, height.Result);
    }

    [Fact]
    public void OperatorBoltEditsClearOnlyTheSelectedFailureAndKeepInspectionResults()
    {
        var assembly = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        var pickup = Guid.NewGuid();
        var shooting = Guid.NewGuid();
        var good = Guid.NewGuid();
        var measured = new BoltResult(false, 8)
        {
            Controller = new("COM9", 1, 1, 1000, 1, 8, 800, 0, 0, 360, 1, 0, 0, 1, 0, null),
        };
        var goodResult = measured with { Success = true };
        assembly.RecordBolt(FasteningHead.Pickup, pickup, new(false, 2));
        assembly.RecordBolt(FasteningHead.Shooting, shooting, measured);
        assembly.RecordBolt(FasteningHead.Shooting, good, goodResult);
        assembly.PcbBarcode = null;
        assembly.CompleteInspection();
        assembly.CompleteFastening();
        Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);

        assembly.ChangeBoltResult(FasteningHead.Pickup, pickup, new(true, null, BoltResultSource.Manual));
        Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
        Assert.Same(measured, assembly.ShootingBoltResults[shooting]);
        assembly.ChangeBoltResult(FasteningHead.Shooting, shooting, null);
        Assert.False(assembly.ShootingBoltResults.ContainsKey(shooting));
        Assert.Same(goodResult, assembly.ShootingBoltResults[good]);
        Assert.Equal(AssemblyResult.Pending, assembly.FasteningResult);
        assembly.CompleteFastening();
        Assert.Equal(AssemblyResult.Ok, assembly.FasteningResult);
        Assert.Equal(AssemblyResult.Ng, assembly.InspectionResult);
        Assert.Equal(AssemblyResult.Ng, assembly.Result);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(36000d)]
    [InlineData(null)]
    public void MeasuredTurnsDoNotChangeHeightJudgment(double? angle)
    {
        var id = Guid.NewGuid();
        var result = new BoltResult(true, 8)
        {
            Controller = angle is { } measured ? new("COM9", 1, 1, 1000, 1, 8, 800, 100, 200, measured, 1, 0, 0, 1, 0, null) : null,
        };
        var assembly = new HeatSinkAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Pickup, id, result);
        assembly.CompleteFastening();
        assembly.PcbBarcode = "PCB-OK";
        assembly.RecordHeight(id, new(DateTimeOffset.Now, new(0, 0, 0.5), 0, 0, 1));
        assembly.CompleteInspection();

        Assert.Equal(angle / 360, result.TotalTurns);
        Assert.Equal(AssemblyResult.Ok, assembly.Result);
        assembly.RecordHeight(id, new(DateTimeOffset.Now, new(0, 0, 2), 0, 0, 1));
        Assert.Equal(AssemblyResult.Ng, assembly.Result);
    }
}
