using System;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class BoltControllerWiringTests
{
    [Fact]
    public async Task FailedStartDoesNotFeedAndStillTurnsStartOff()
    {
        var settings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(settings.Outputs, new());
        var head = VirtualTest.CreateAdcHead(new AdcControllerStub(), io, FasteningHead.Pickup, new(), 1, "Virtual", 115200);
        await head.SelectPresetAsync(1);
        var failure = new IOException("START write failed.");
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart && on)
                throw failure;
        };
        var fed = false;
        Task FeedAsync(CancellationToken token)
        {
            fed = true;
            return Task.CompletedTask;
        }

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(
            () => head.TightenAsync(feedAsync: FeedAsync)));
        Assert.False(fed);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Theory]
    [InlineData(BoltDriver.Virtual)]
    [InlineData(BoltDriver.HantasAdc)]
    public async Task DriverSelectionChangesControllerButKeepsAllIoAvailable(BoltDriver driver)
    {
        var settings = new MachineSettings();
        settings.Drivers.Bolt = driver;
        await using var services = new ServiceCollection().AddIbtmApplication(settings).BuildServiceProvider();
        var signals = services.GetRequiredService<IoSignals>();
        foreach (var head in Enum.GetValues<FasteningHead>())
        {
            var controller = services.GetRequiredKeyedService<IBoltHead>(head);
            Assert.IsType<AdcBoltHead>(controller);
        }
        Assert.NotNull(services.GetKeyedService<IAdcBus>(FasteningHead.Pickup));
        Assert.NotNull(services.GetKeyedService<IAdcBus>(FasteningHead.Shooting));
        Assert.Null(services.GetService<IAdcBus>());
        var io = services.GetRequiredService<IIoService>();
        var machine = services.GetRequiredService<MachineController>();
        var inputs = new InputWindowViewModel(io, signals);
        var outputs = new OutputWindowViewModel(signals, machine);
        Assert.Empty(settings.IoBoltHardware.Inputs);
        Assert.DoesNotContain(inputs.Filter.FilteredRows.Cast<InputControlRow>(),
            row => (int)row.Io.Signal is >= 83 and <= 88);
        foreach (var signal in settings.IoBoltHardware.Outputs.Keys)
        {
            Assert.Contains(
                outputs.Filter.FilteredRows.Cast<OutputWindowRow>(),
                row => row.Io.Signal == signal);
        }

        settings.Drivers.Bolt = driver == BoltDriver.Virtual ? BoltDriver.HantasAdc : BoltDriver.Virtual;
        foreach (var signal in new[] { OutputIo.PickupBoltStart, OutputIo.ShootingBoltStart })
        {
            var row = Assert.Single(outputs.Rows, row => row.Io.Signal == signal);
            row.ToggleCommand.Execute(null);
            Assert.Null(row.ActionMessage);
            Assert.True(io.GetOutput(signal));
            row.ToggleCommand.Execute(null);
            Assert.Null(row.ActionMessage);
            Assert.False(io.GetOutput(signal));
        }
        // Diagnostics and manual outputs remain available with ADC result collection.
        Assert.NotNull(services.GetRequiredService<DiagnosticWindows>());
    }

    [Fact]
    public void OldSettingsDropRetiredInputsAndKeepOutputAddresses()
    {
        var settings = JsonSerializer.Deserialize<IoBoltHardwareSettings>("""
            {"Inputs":{"PickupBoltReady":11,"84":12,"85":13,"86":93,"87":94,"88":95},
             "Outputs":{"ShootingBoltStart":{"Number":115}}}
            """)!;
        Assert.Empty(settings.Inputs);
        Assert.Equal(115, settings.Outputs[OutputIo.ShootingBoltStart].Number);
        Assert.DoesNotContain("Inputs", JsonSerializer.Serialize(settings));
        var drivers = JsonSerializer.Deserialize<DriverSettings>("""{"Bolt":"Io"}""")!;
        Assert.Equal(BoltDriver.HantasAdc, drivers.Bolt);
    }
}
