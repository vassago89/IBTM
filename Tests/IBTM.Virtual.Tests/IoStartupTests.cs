using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class IoStartupTests
{
    [Fact]
    public async Task PhysicalInputWithoutAValidScanIsNotOffOrCompleted()
    {
        // Construction and unavailable reads must not invoke either native SDK.
        using var physical = new PhysicalIoService(
            new IBTM.AlphaMotion.AlphaMotionController(new()),
            new IBTM.Ajin.AjinController(new()),
            new Dictionary<InputIo, int>(),
            new Dictionary<OutputIo, OutputHardware>(),
            new());
        IIoService io = physical;

        Assert.Throws<IOException>(() => io.GetInput(InputIo.PcbPlacementCarrierPresent));
        await Assert.ThrowsAsync<IOException>(
            () => io.WaitForInputAsync(InputIo.PcbPlacementCarrierPresent, false));
    }

    [Fact]
    public async Task InputFaultStopsReachableOutputsAndManualStopCanRetry()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        var physicalOutputs = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.AllowWritesWhileUnavailable = true;
            machine.ToggleDiagnosticOutput(OutputIo.MainConveyorRun);
            Assert.True(physicalOutputs.GetOutput(OutputIo.MainConveyorRun));

            io.Disconnect(new IOException("Input controller disconnected; motor controller is still connected."));
            Assert.False(io.IsReady);
            Assert.False(physicalOutputs.GetOutput(OutputIo.MainConveyorRun));

            physicalOutputs.SetOutput(OutputIo.MainConveyorRun, true);
            machine.StopManualConveyor(OutputIo.MainConveyorRun);
            Assert.False(physicalOutputs.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(OutputBlockReason.IoUnavailable, machine.ToggleDiagnosticOutput(OutputIo.MainConveyorRun));
            Assert.Equal(0, io.ReadsWhileUnavailable);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StopAttemptsEveryDeviceAndPreservesWriteFailures()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var writes = new List<OutputIo>();
        var conveyorFailure = new IOException("Main conveyor STOP failed.");
        var shootingFailure = new IOException("Shoot output OFF failed.");
        io.BeforeOutputWrite = (output, value) =>
        {
            Assert.False(value);
            writes.Add(output);
            if (output == OutputIo.MainConveyorRun)
                throw conveyorFailure;
            if (output == OutputIo.ShootBolt)
                throw shootingFailure;
        };
        try
        {
            var failure = Assert.Throws<AggregateException>(machine.Stop);
            Assert.Equal(new[] { conveyorFailure, shootingFailure }, failure.InnerExceptions);
            Assert.Contains(OutputIo.ShootingFeederRunSignal, writes);
            Assert.Contains(OutputIo.NgConveyorRun, writes);
            Assert.Contains(OutputIo.PcbSupplyReadyToFront1, writes);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(OutputIo.MainConveyorRun)]
    [InlineData(OutputIo.NgConveyorRun)]
    public async Task ManualConveyorReadFailureWaitsForDeviceCleanup(OutputIo output)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var io = services.GetRequiredService<StartupIo>();
        var outputs = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await state.StopDisplayUpdatesAsync(); // Isolate the command's output read.
        outputs.AutoResponseEnabled = false;
        var readFailure = new IOException("Manual RUN output read failed.");
        var cleanupOutput = output == OutputIo.MainConveyorRun
            ? OutputIo.MainConveyorAvailableToRear
            : OutputIo.NgCarrierEjectCompleteLamp;
        var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim();
        var started = false;
        var cleanupWrites = 0;
        io.BeforeOutputWrite = (signal, value) =>
        {
            if (signal == output && value)
                started = true;
            if (started && signal == cleanupOutput && !value)
            {
                Interlocked.Increment(ref cleanupWrites);
                cleanupReached.TrySetResult();
                Assert.True(releaseCleanup.Wait(TimeSpan.FromSeconds(2)));
            }
        };
        io.OutputReadError = readFailure;
        var run = Task.Run(() => machine.RunManualConveyorAsync(output, CancellationToken.None));
        try
        {
            await cleanupReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(outputs.GetOutput(output));
            Assert.False(run.IsCompleted);
            Assert.True(operations.HasActiveOperations);
            releaseCleanup.Set();
            Assert.Same(
                readFailure,
                await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(2))));
            Assert.Equal(1, cleanupWrites);
            Assert.False(operations.HasActiveOperations);
            Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        }
        finally
        {
            releaseCleanup.Set();
            io.BeforeOutputWrite = null;
            io.OutputReadError = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public void StateQueriesBeforeInitializationDoNotReadOutputs()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();

        Assert.False(state.IsRunning);
        Assert.False(state.ConveyorRunning);
        Assert.False(machine.CanStart);
        Assert.False(machine.CanHome);
        _ = machine.CanReset;
        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            Assert.True(state.IsRunning);
            Assert.False(machine.CanReset);
        }

        Assert.Equal(0, io.ReadsWhileUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationFailurePreservesOriginalAlarmAndAllowsReset(bool failCheckReady)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        var error = new IOException("Original control initialization failure.");
        io.InitializationError = error;
        io.FailCheckReady = failCheckReady;

        await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));

        AssertUnavailable(state, error);
        var log = services.GetRequiredService<ApplicationLog>();
        var detail = Assert.Single(log.Snapshot(), entry => entry.Detail?.Contains(error.Message) == true);
        Assert.Equal("Machine alarm: IoCommunication.", detail.Message);
        Assert.Equal(error.ToString(), detail.Detail);
        var stage = failCheckReady ? "Control I/O readiness check" : "Control I/O initialization";
        Assert.Contains(log.Snapshot(), entry => entry.Message == $"{stage} failed. {error.Message}"
            && entry.Detail is null);
        Assert.True(machine.CanReset);
        Assert.False(machine.CanStart);
        Assert.False(machine.CanHome);
        Assert.False(state.ManualSetupEnabled);
        Assert.All(
            services.GetRequiredService<IoSignals>().Outputs.Values,
            output => Assert.Null(output.IsOn));
        Assert.Equal(0, io.ReadsWhileUnavailable);
        Assert.Equal(0, io.WritesWhileUnavailable);

        var resetError = new IOException("Control initialization failed again during RESET.");
        io.InitializationError = resetError;
        await machine.ResetAsync().WaitAsync(TimeSpan.FromSeconds(2));
        AssertUnavailable(state, error);
        var resetDetail = Assert.Single(log.Snapshot(), entry => entry.Detail?.Contains(resetError.Message) == true);
        Assert.Equal("Machine alarm remains: IoCommunication.", resetDetail.Message);
        Assert.Equal(resetError.ToString(), resetDetail.Detail);

        io.InitializationError = null;
        await machine.ResetAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => state.Display.Available && state.Display.Alarm == MachineAlarm.None);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Null(state.Display.ReadError);
        Assert.Equal(0, io.ReadsWhileUnavailable);
        Assert.Equal(0, io.WritesWhileUnavailable);
        await machine.ShutdownAsync();
    }

    [Fact]
    public async Task LostConnectionDoesNotReplaceTheOriginalFaultWithAnOutputRead()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var error = new IOException("Original control connection failure.");

        io.Disconnect(error);
        await WaitUntilAsync(() => !state.Display.Available);

        AssertUnavailable(state, error);
        Assert.Contains(
            services.GetRequiredService<ApplicationLog>().Snapshot(),
            entry => entry.Level == "ERROR" && entry.Detail?.Contains(error.Message) == true);
        Assert.True(machine.CanReset);
        Assert.All(
            services.GetRequiredService<InspectionGantry>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));
        Assert.Equal(0, io.ReadsWhileUnavailable);
        await Assert.ThrowsAsync<AggregateException>(machine.ShutdownAsync);
        Assert.True(io.WritesWhileUnavailable > 0);
    }

    [Fact]
    public async Task ConnectionFailureDuringDisplayReadPreservesTheOriginalFault()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var error = new IOException("Original connection fault during display scan.");
        io.BeforeOutputRead = () =>
        {
            io.Disconnect(error);
            throw new IOException("Secondary AXT_RT_NOT_OPEN during the in-flight read.");
        };

        state.RequestDisplayRefresh();
        await WaitUntilAsync(() => !state.Display.Available);

        AssertUnavailable(state, error);
        Assert.All(
            services.GetRequiredService<InspectionGantry>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));
        Assert.Equal(0, io.ReadsWhileUnavailable);
        await Assert.ThrowsAsync<AggregateException>(machine.ShutdownAsync);
    }

    [Fact]
    public async Task ReadyOutputReadFailureStillFailsClosed()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var error = new IOException("Actual output read failure.");

        io.OutputReadError = error;
        Assert.Same(error, Assert.Throws<IOException>(() => machine.CanStart));
        state.RequestDisplayRefresh();
        await WaitUntilAsync(() => ReferenceEquals(error, state.Display.ReadError));

        Assert.False(state.Display.CanStart);
        Assert.False(state.Display.ManualSetupEnabled);
        io.OutputReadError = null;
        await machine.ShutdownAsync();
    }

    private static void AssertUnavailable(MachineState state, Exception error)
    {
        Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        Assert.False(state.Display.Available);
        Assert.Equal(MachineAlarm.IoCommunication, state.Display.Alarm);
        Assert.Equal(error.Message, state.Display.AlarmMessage);
        Assert.Contains(error.Message, state.Display.AlarmDetail);
        Assert.Null(state.Display.ReadError);
        Assert.False(state.Display.CanStart);
        Assert.False(state.Display.CanHome);
        Assert.False(state.Display.ManualControlsEnabled);
        Assert.False(state.Display.ManualSetupEnabled);
    }

    private static ServiceProvider CreateServices()
    {
        return new ServiceCollection().AddIbtmApplication(
            new MachineSettings { Drivers = new() { Inspection = InspectionAlgorithm.Virtual }, })
            .AddSingleton<StartupIo>()
            .AddSingleton<IIoService>(provider => provider.GetRequiredService<StartupIo>())
            .BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    // Physical I/O starts closed; the regular VirtualIoService starts ready and
    // permits output reads while disconnected, which cannot expose this bug.
    private sealed class StartupIo(VirtualIoService inner) : IIoService
    {
        public bool IsReady { get; private set; }

        public int TimeoutMilliseconds
        {
            get
            {
                return inner.TimeoutMilliseconds;
            }
        }

        public Exception? InitializationError { get; set; }
        public Exception? OutputReadError { get; set; }
        public Action? BeforeOutputRead { get; set; }
        public Action<OutputIo, bool>? BeforeOutputWrite { get; set; }
        public bool FailCheckReady { get; set; }
        public bool AllowWritesWhileUnavailable { get; set; }
        public int ReadsWhileUnavailable { get; private set; }
        public int WritesWhileUnavailable { get; private set; }

        public event Action<Exception>? Faulted;
        public event Action<InputIo, bool>? InputChanged
        {
            add
            {
                inner.InputChanged += value;
            }

            remove
            {
                inner.InputChanged -= value;
            }
        }

        public event Action<OutputIo, bool>? OutputChanged
        {
            add
            {
                inner.OutputChanged += value;
            }

            remove
            {
                inner.OutputChanged -= value;
            }
        }

        public void Initialize()
        {
            if (!FailCheckReady && InitializationError is { } error)
                throw error;
            IsReady = true;
            inner.Initialize();
        }

        public void CheckReady()
        {
            if (FailCheckReady && InitializationError is { } error)
            {
                IsReady = false;
                throw error;
            }

            inner.CheckReady();
        }

        public void Disconnect(Exception error)
        {
            IsReady = false;
            Faulted?.Invoke(error);
        }

        public bool GetInput(InputIo input)
        {
            if (!IsReady)
            {
                ReadsWhileUnavailable++;
                throw new IOException("Input scan is unavailable; cached inputs are not current feedback.");
            }

            return inner.GetInput(input);
        }

        public OutputFeedback? GetOutputFeedback(OutputIo output)
        {
            return inner.GetOutputFeedback(output);
        }

        public bool GetOutput(OutputIo output)
        {
            if (!IsReady)
            {
                ReadsWhileUnavailable++;
                throw new IOException("Simulated AXT_RT_NOT_OPEN during output read.");
            }

            var beforeRead = BeforeOutputRead;
            BeforeOutputRead = null;
            beforeRead?.Invoke();
            if (OutputReadError is { } error)
                throw error;
            return inner.GetOutput(output);
        }

        public void SetOutput(OutputIo output, bool value)
        {
            if (!IsReady)
            {
                WritesWhileUnavailable++;
                if (!AllowWritesWhileUnavailable)
                    throw new IOException("Output controller is unavailable.");
            }

            BeforeOutputWrite?.Invoke(output, value);
            inner.SetOutput(output, value);
        }
    }
}
