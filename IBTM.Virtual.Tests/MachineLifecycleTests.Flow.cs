using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Inspection.Training;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Station3ConfigurationRunsWithoutUpstreamHardware(bool missingBolts)
    {
        var settings = new MachineSettings
        {
            Units = new()
            {
                MainConveyor = true,
                Inspection = true,
                NgCarrierTransfer = true,
                NgShuttle = true,
                NgConveyor = true,
                PcbSupply = false,
                PcbPlacement = false,
                BoltFastening = false,
                PickupBoltFeeder = false,
                ShootingBoltFeeder = false,
            },
            Drivers = new() { Inspection = InspectionAlgorithm.Virtual },
        };
        FastHomes(settings);
        settings.InspectionGantry.Motion = FastMotion();
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 20, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 100, Y = 20 };
        settings.NgCarrierTransfer.Speed = 10_000;
        using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<Recipe>();
        recipe.Pcb.BoltPoints = [new() { Number = 1, X = 10, Y = 10 },];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionWork>();
        IMotionFeedback[] disabledMotions = [
            services.GetRequiredService<PcbSupplyHandler>().Feedback,
            services.GetRequiredService<PcbPlacementHandler>().Feedback,
            services.GetRequiredService<BoltFasteningGantry>().Feedback,
        ];
        var disabledOutputs = settings.PcbSupplyHardware.Outputs.Keys.Concat(
            settings.PcbPlacementHandlerHardware.Outputs.Keys)
            .Concat(settings.BoltFasteningHardware.Outputs.Keys)
            .Concat(settings.BoltFeederHardware.Outputs.Keys)
            .ToHashSet();
        var unexpectedOutputs = new ConcurrentBag<OutputIo>();
        var adcFrames = 0;
        services.GetRequiredService<IAdcBus>().FrameTransferred += (_, _) => Interlocked.Increment(
            ref adcFrames);
        io.OutputChanged += (output, value) =>
        {
            if (value && disabledOutputs.Contains(output))
                unexpectedOutputs.Add(output);
        };
        var arrived = 0;
        var entered = false;
        var exited = false;
        io.InputChanged += (input, value) =>
        {
            if (value)
            {
                Interlocked.Or(
                    ref arrived,
                    input switch
                    {
                        InputIo.PcbPlacementCarrierPresent => 1,
                        InputIo.BoltFasteningCarrierPresent => 2,
                        InputIo.InspectionCarrierPresent => 4,
                        _ => 0,
                    });
            }

            if (input == InputIo.MainConveyorEntryCarrierDetected && value)
                entered = true;
            if (input == InputIo.MainConveyorAvailableFromFront2 && value && entered)
                io.SetInput(input, false);
            if (input == InputIo.MainConveyorExitCarrierDetected && !value)
                exited = true;
        };
        if (missingBolts)
        {
            var camera = services.GetRequiredService<VirtualCamera>();
            camera.BoltsPresent = false;
        }

        await machine.InitializeAsync();
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.CanStart);
        var run = machine.StartAsync();
        try
        {
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => inspection.Completed, TimeSpan.FromSeconds(10)));
            Assert.Equal(7, arrived);
            Assert.Equal(missingBolts, inspection.HasNg);
            Assert.Equal(2, inspection.Assemblies.Count());
            Assert.All(
                inspection.Assemblies,
                assembly =>
                {
                    Assert.Equal(
                        assembly.HeatSink == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2",
                        assembly.PcbBarcode);
                    Assert.Empty(assembly.PcbBoltResults);
                    Assert.Empty(assembly.IpmSeatingResults);
                    Assert.Empty(assembly.IpmFinalResults);
                    Assert.Equal(!missingBolts, Assert.Single(assembly.BoltPresenceResults).Value);
                });
            if (missingBolts)
            {
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => io.GetInput(InputIo.NgConveyorPosition1Occupied)
                            && io.GetInput(InputIo.NgShuttleUp)
                            && !io.GetOutput(OutputIo.NgConveyorRun),
                        TimeSpan.FromSeconds(5)));
                Assert.False(exited);
            }
            else
            {
                Assert.False(exited);
                io.SetInput(InputIo.MainConveyorReadyFromRear, true);
                await WaitUntilAsync(() => exited);
            }
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Empty(unexpectedOutputs);
        Assert.Equal(0, adcFrames);
        Assert.All(
            disabledMotions,
            motion =>
                Assert.All(
                    motion.Axes,
                    axis =>
                    {
                        Assert.False(motion.GetAxisState(axis).ServoOn);
                        Assert.False(motion.GetAxisState(axis).Homed);
                    }));
    }

}
