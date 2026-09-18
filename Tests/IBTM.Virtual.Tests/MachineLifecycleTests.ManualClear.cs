using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Fact]
    public async Task AutomaticStopRequiresExplicitEmptyMachineResetEvenWithMainConveyorDisabled()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(machine.CanStart, machine.StartBlock.ToString());
        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(StartBlockReason.ManualClearRequired, machine.StartBlock);
            Assert.True(machine.CanReset);

            var writes = 0;
            void CountOutput(OutputIo output, bool value)
            {
                writes++;
            }
            io.OutputChanged += CountOutput;
            await machine.StartAsync();
            io.OutputChanged -= CountOutput;
            Assert.Equal(0, writes);

            io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            await machine.ResetAsync();
            Assert.True(machine.RequiresManualClear);
            Assert.True(state.IsError);
            io.SetInput(InputIo.PcbPlacementPcbDetected, false);
            Assert.True(machine.RequiresManualClear);
            // Disabling Supply must not hide the physical upstream carrier at RESET.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            await machine.ResetAsync();
            Assert.True(machine.RequiresManualClear);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            await machine.ResetAsync();
            Assert.False(machine.RequiresManualClear);
            Assert.False(state.AutomaticRunning);
            Assert.True(machine.CanStart, state.AlarmDetail);

            using var nextStop = new CancellationTokenSource();
            run = machine.StartAsync(nextStop.Token);
            await WaitUntilAsync(() => state.AutomaticRunning);
            nextStop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(machine.RequiresManualClear);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }
}
