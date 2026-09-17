using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ProductionExcludesEachDisabledFeederAndItsHead(bool pickupEnabled, bool shootingEnabled)
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = BoltDriver.Io;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = pickupEnabled;
        settings.Units.ShootingBoltFeeder = shootingEnabled;
        var disabledPickup = new WaitingBoltHead { WaitForReadiness = true };
        var disabledShooting = new WaitingBoltHead { WaitForReadiness = true };
        var registrations = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings, new Recipe());
        if (!pickupEnabled)
            registrations.AddKeyedSingleton<IBoltHead>(FasteningHead.Pickup, disabledPickup);
        if (!shootingEnabled)
            registrations.AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, disabledShooting);
        using var services = registrations.BuildServiceProvider();
        var recipe = services.GetRequiredService<Recipe>();
        recipe.Pcb.BoltPoints = [
            new() { Number = 1, Head = FasteningHead.Shooting, X = shootingEnabled ? 10 : null, Y = 10 },
            new() { Number = 2, Head = FasteningHead.Pickup, X = pickupEnabled ? 20 : null, Y = 10 },
        ];
        recipe.BoltFastening.PcbPreset = 1;
        recipe.BoltFastening.IpmSeatingPreset = 2;
        recipe.BoltFastening.IpmFinalPreset = 3;
        settings.BoltFastening.ShootingHead.FasteningZ = 8;
        settings.BoltFastening.PickupHead.FasteningZ = 12;
        if (!pickupEnabled)
            settings.BoltFastening.PickupHead.UpperLeftLocatingPin = null;
        if (!shootingEnabled)
            settings.BoltFastening.ShootingHead.UpperLeftLocatingPin = null;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        var outputs = new ConcurrentQueue<(OutputIo Output, bool On)>();
        await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, pickupEnabled);
        io.SetInput(InputIo.ShootingFeederBoltDetected, shootingEnabled);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        io.OutputChanged += (output, on) =>
        {
            outputs.Enqueue((output, on));
            if (output == OutputIo.ShootingBoltStart)
                io.SetInput(InputIo.ShootingBoltFasten, on);
            if (output == OutputIo.PickupBoltStart)
                io.SetInput(InputIo.PickupBoltFasten, on);
            if (output == OutputIo.ShootingHeadUp && !on)
            {
                Assert.True(io.GetOutput(OutputIo.ShootingBoltStart));
                io.SetInput(InputIo.ShootingBoltFasten, false);
            }
            if (output == OutputIo.PickupHeadUp && !on && io.GetOutput(OutputIo.PickupBoltStart))
                io.SetInput(InputIo.PickupBoltFasten, false);
        };
        work.Changed += () =>
        {
            if (work.Completed)
                machine.Stop();
        };
        try
        {
            Assert.False(state.RepeatEnabled);
            Assert.True(machine.CanStart, machine.StartBlock.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(work.Completed, services.GetRequiredService<BoltFasteningStation>().State().ToString());
            var assembly = Assert.Single(work.Assemblies);
            Assert.Equal(shootingEnabled ? 1 : 0, assembly.PcbBoltResults.Count);
            Assert.Equal(pickupEnabled ? 1 : 0, assembly.IpmSeatingResults.Count);
            Assert.Equal(pickupEnabled ? 1 : 0, assembly.IpmFinalResults.Count);
            Assert.All(assembly.PcbBoltResults.Values.Concat(assembly.IpmSeatingResults.Values)
                .Concat(assembly.IpmFinalResults.Values), result => Assert.Equal(BoltResultSource.IoAssumedOk, result.Source));
            Assert.Equal(AssemblyResult.Pending, assembly.FasteningResult);
            if (!pickupEnabled)
                Assert.DoesNotContain(outputs, command =>
                    command.On && command.Output is OutputIo.PickupBoltStart or OutputIo.PickupHeadVacuumPump
                        or OutputIo.PickupBoltPreset1 or OutputIo.PickupBoltPreset2 or OutputIo.PickupBoltPreset3
                    || !command.On && command.Output == OutputIo.PickupHeadUp);
            if (!shootingEnabled)
                Assert.DoesNotContain(outputs, command =>
                    command.On && command.Output is OutputIo.ShootingBoltStart or OutputIo.ShootingHeadVacuumPump
                        or OutputIo.ShootingBoltPreset1 or OutputIo.ShootingBoltPreset2 or OutputIo.ShootingBoltPreset3
                        or OutputIo.ShootBolt or OutputIo.ShootingEscapeForward or OutputIo.ShootingFeederRunSignal
                    || !command.On && command.Output == OutputIo.ShootingHeadUp);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            Assert.True(io.GetInput(InputIo.PickupHeadUp));
            Assert.True(io.GetInput(InputIo.ShootingHeadUp));
            state.SetError(MachineAlarm.BoltFastening, new InvalidOperationException("Reset verification"));
            await machine.ResetAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Equal(0, disabledPickup.ReadinessChecks);
            Assert.Equal(0, disabledShooting.ReadinessChecks);
            if (!pickupEnabled && !shootingEnabled)
            {
                recipe.Pcb.BoltPoints.Clear();
                settings.CarrierReference.UpperLeftLocatingPin = null;
                Assert.True(machine.TeachingReady);
            }
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }
}
