using System;
using System.IO;
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

public sealed class IoBoltHeadTests
{
    [Fact]
    public async Task FailedStartDoesNotFeedAndStillTurnsStartOff()
    {
        var settings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(settings.Outputs, new());
        using var head = new IoBoltHead(io, FasteningHead.Pickup, settings);
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
        Assert.True(head.HasPendingResult);
        Assert.Null(await head.ReadPendingResultAsync());
    }

    [Theory]
    [InlineData(BoltDriver.Io)]
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
            if (driver == BoltDriver.Io)
                Assert.IsType<IoBoltHead>(controller);
            else
                Assert.IsType<AdcBoltHead>(controller);
        }
        Assert.Equal(driver != BoltDriver.Io, services.GetKeyedService<IAdcBus>(FasteningHead.Pickup) is not null);
        Assert.Equal(driver != BoltDriver.Io, services.GetKeyedService<IAdcBus>(FasteningHead.Shooting) is not null);
        Assert.Null(services.GetService<IAdcBus>());
        var io = services.GetRequiredService<IIoService>();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var inputs = new InputWindowViewModel(io, signals);
        var outputs = new OutputWindowViewModel(signals, machine);
        foreach (var signal in settings.IoBoltHardware.Inputs.Keys)
        {
            Assert.Contains(
                inputs.Filter.FilteredRows.Cast<InputControlRow>(),
                row => row.Io.Signal == signal);
        }
        foreach (var signal in settings.IoBoltHardware.Outputs.Keys)
        {
            Assert.Contains(
                outputs.Filter.FilteredRows.Cast<OutputWindowRow>(),
                row => row.Io.Signal == signal);
        }

        settings.Drivers.Bolt = driver == BoltDriver.Io ? BoltDriver.HantasAdc : BoltDriver.Io;
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
        // Manual diagnostics must still resolve when there is no serial bus.
        Assert.NotNull(services.GetRequiredService<DiagnosticWindows>());
    }

    [Fact]
    public async Task FastenOnThenOffReturnsAssumedOkWithoutTorque()
    {
        var settings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(settings.Outputs, new());
        using var head = new IoBoltHead(io, FasteningHead.Pickup, settings);
        // These raw inputs do not judge or admit this IO-only cycle.
        io.SetInput(InputIo.PickupBoltAlarm, true);
        io.SetOutput(OutputIo.PickupBoltPreset1, true);
        await head.SelectPresetAsync(3);
        Assert.False(io.GetOutput(OutputIo.PickupBoltPreset1));
        Assert.False(io.GetOutput(OutputIo.PickupBoltPreset2));
        Assert.True(io.GetOutput(OutputIo.PickupBoltPreset3));
        var cycle = head.TightenAsync();
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(cycle.IsCompleted); // Initial OFF is not completion.
        io.SetInput(InputIo.PickupBoltFasten, true);
        Assert.False(cycle.IsCompleted); // ON alone is not completion either.
        io.SetInput(InputIo.PickupBoltFasten, false);
        var result = await cycle.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Success);
        Assert.Null(result.Torque);
        Assert.Equal(BoltResultSource.IoAssumedOk, result.Source);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(head.HasPendingResult);
        Assert.Null(await head.ReadPendingResultAsync());
    }

    [Fact]
    public async Task StaleFastenAndChangedPresetCannotStartAnotherCycle()
    {
        var settings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(settings.Outputs, new());
        using var head = new IoBoltHead(io, FasteningHead.Pickup, settings);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => head.SelectPresetAsync(4));
        await head.SelectPresetAsync(2);
        io.SetOutput(OutputIo.PickupBoltPreset1, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        io.SetOutput(OutputIo.PickupBoltPreset1, false);
        io.SetInput(InputIo.PickupBoltFasten, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(head.HasPendingResult);
    }

    [Fact]
    public async Task ShootingHeadRetainsShortFastenPulseDuringStartWrite()
    {
        var settings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(settings.Outputs, new());
        using var head = new IoBoltHead(io, FasteningHead.Shooting, settings);
        await head.SelectPresetAsync(1);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingBoltStart && on)
            {
                io.SetInput(InputIo.ShootingBoltFasten, true);
                io.SetInput(InputIo.ShootingBoltFasten, false);
            }
        };
        Assert.True((await head.TightenAsync()).Success);
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
    }

    [Fact]
    public async Task RestartAfterCancellationRequiresANewFastenCycle()
    {
        var settings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(settings.Outputs, new());
        using var head = new IoBoltHead(io, FasteningHead.Pickup, settings);
        await head.SelectPresetAsync(1);
        var starts = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.PickupBoltStart)
                return;
            if (on)
                starts++;
            else
                io.SetInput(InputIo.PickupBoltFasten, false);
        };
        using var cancellation = new CancellationTokenSource();
        var cycle = head.TightenAsync(cancellation.Token);
        io.SetInput(InputIo.PickupBoltFasten, true);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(io.GetInput(InputIo.PickupBoltFasten));
        Assert.True(head.HasPendingResult);
        Assert.Null(await head.ReadPendingResultAsync());
        Assert.Equal(1, starts);
        var restarted = head.TightenAsync();
        Assert.Equal(2, starts);
        Assert.False(restarted.IsCompleted);
        io.SetInput(InputIo.PickupBoltFasten, true);
        Assert.False(restarted.IsCompleted);
        io.SetInput(InputIo.PickupBoltFasten, false);
        Assert.True((await restarted.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.False(head.HasPendingResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectStartOffDoesNotCountStopInducedFastenOffAsOk(bool feedbackArrivesFirst)
    {
        var settings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(settings.Outputs, new());
        void EndFastenOnStop(OutputIo output, bool on)
        {
            if (output == OutputIo.PickupBoltStart && !on)
                io.SetInput(InputIo.PickupBoltFasten, false);
        }
        if (feedbackArrivesFirst)
            io.OutputChanged += EndFastenOnStop;
        using var head = new IoBoltHead(io, FasteningHead.Pickup, settings);
        if (!feedbackArrivesFirst)
            io.OutputChanged += EndFastenOnStop;
        await head.SelectPresetAsync(1);
        var cycle = head.TightenAsync();
        io.SetInput(InputIo.PickupBoltFasten, true);

        io.SetOutput(OutputIo.PickupBoltStart, false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cycle.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.True(head.HasPendingResult);
        Assert.Null(await head.ReadPendingResultAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingEitherFastenEdgeTimesOutAndStops(bool sawOn)
    {
        var settings = new IoBoltHardwareSettings { FasteningTimeoutMilliseconds = 50 };
        var io = new VirtualIoService(settings.Outputs, new());
        using var head = new IoBoltHead(io, FasteningHead.Pickup, settings);
        await head.SelectPresetAsync(1);
        var cycle = head.TightenAsync();
        if (sawOn)
            io.SetInput(InputIo.PickupBoltFasten, true);
        await Assert.ThrowsAsync<TimeoutException>(() => cycle);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Null(await head.ReadPendingResultAsync());
    }
}
