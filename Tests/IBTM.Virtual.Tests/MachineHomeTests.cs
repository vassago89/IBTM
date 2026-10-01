using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static IBTM.Virtual.Tests.MachineTestSupport;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

[Collection("Machine integration")]
public sealed class MachineHomeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementHomeReportsHorizontalFailureAfterZHome(bool allUnits)
    {
        var settings = FlowSettings();
        if (!allUnits)
            settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await machine.InitializeAsync();
        try
        {
            settings.PcbPlacementHandler.Motion.HorizontalHome.SearchSpeed = 0;
            await WaitUntilAsync(() => teaching.IsHomeAllowed);

            if (allUnits)
                await machine.HomeAsync(CancellationToken.None);
            else
                await teaching.HomeCommand.ExecuteAsync(null);

            Assert.True(motion.GetAxisState(MotionAxis.Z).Homed);
            Assert.False(motion.GetAxisState(MotionAxis.X).Homed);
            Assert.False(motion.GetAxisState(MotionAxis.Y).Homed);
            if (allUnits)
            {
                foreach (var (group, other) in services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>())
                {
                    if (group != MotionGroup.PcbPlacementHandler)
                        Assert.All(other.Axes, axis => Assert.False(other.GetAxisState(axis).Homed));
                }
            }
            Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
            Assert.Contains(nameof(ArgumentOutOfRangeException), state.AlarmDetail);
            Assert.False(state.IsHoming);
            Assert.False(motion.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SelectedAxisHomeDoesNotRequireZHomeOrServo()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        await machine.InitializeAsync();
        try
        {
            motion.SetServo(MotionAxis.Z, false);
            await machine.HomeAsync(MotionGroup.PcbSupply, default, MotionAxis.X);

            Assert.True(motion.GetAxisState(MotionAxis.X).Homed);
            Assert.False(motion.GetAxisState(MotionAxis.Z).Homed);
            Assert.False(motion.GetAxisState(MotionAxis.Z).ServoOn);
            Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementHomeRunsZThenYThenXBeforeOtherUnits(bool allUnits)
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.HandoffPosition.Z = 8;
        settings.BoltFastening.SafeZ = 12;
        settings.PcbSupply.TravelZ = 16;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var outputs = new ConcurrentQueue<OutputIo>();
        services.GetRequiredService<VirtualIoService>().OutputChanged += (output, _) => outputs.Enqueue(output);
        var motions = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>();
        var placement = motions[MotionGroup.PcbPlacementHandler];
        var placementHomeOrder = new List<MotionAxis>();
        placement.StateChanged += () =>
        {
            foreach (var axis in placement.Axes)
            {
                if (placement.GetAxisState(axis).Homed && !placementHomeOrder.Contains(axis))
                    placementHomeOrder.Add(axis);
            }
        };
        var starts = new ConcurrentQueue<(MotionGroup Group, bool PlacementHomed)>();
        foreach (var (group, motion) in motions)
        {
            if (group != MotionGroup.PcbPlacementHandler)
                motion.MovingChanged += moving =>
                {
                    if (moving)
                        starts.Enqueue((group, placement.Axes.All(axis => placement.GetAxisState(axis).Homed)));
                };
        }
        try
        {
            if (allUnits)
                await machine.HomeAsync(default);
            else
                await machine.HomeAsync(MotionGroup.PcbPlacementHandler, default);

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Empty(outputs);
            Assert.Equal(new[] { MotionAxis.Z, MotionAxis.Y, MotionAxis.X }, placementHomeOrder);
            Assert.Equal(allUnits ? motions.Count - 1 : 0, starts.Select(start => start.Group).Distinct().Count());
            Assert.All(starts, start => Assert.True(start.PlacementHomed, $"{start.Group} started before Placement HOME completed."));
            foreach (var (group, motion) in motions)
            {
                Assert.Equal((0, 0, 0), motion.Position);
                Assert.All(motion.Axes, axis => Assert.Equal(
                    allUnits || group == MotionGroup.PcbPlacementHandler, motion.GetAxisState(axis).Homed));
            }
            Assert.False(state.IsHoming);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MotionAxis.Y)]
    [InlineData(MotionAxis.X)]
    public async Task StopAfterPlacementHomeAxisPreventsFollowingHomeAndAllowsRestart(MotionAxis stopAfter)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var motions = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>();
        var placement = motions[MotionGroup.PcbPlacementHandler];
        await machine.InitializeAsync();
        var starts = new ConcurrentQueue<MotionGroup>();
        foreach (var (group, motion) in motions)
            motion.MovingChanged += moving =>
            {
                if (moving)
                    starts.Enqueue(group);
            };

        void StopAfterPlacementHome()
        {
            if (!placement.GetAxisState(stopAfter).Homed)
                return;
            placement.StateChanged -= StopAfterPlacementHome;
            machine.Stop();
        }

        placement.StateChanged += StopAfterPlacementHome;
        try
        {
            await machine.HomeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(Enumerable.Repeat(MotionGroup.PcbPlacementHandler, stopAfter == MotionAxis.Y ? 2 : 3), starts);
            Assert.Equal(stopAfter == MotionAxis.X, placement.GetAxisState(MotionAxis.X).Homed);
            Assert.False(state.IsHoming);
            Assert.False(operations.HasActiveOperations);
            Assert.Equal(MachineAlarm.None, state.Alarm);

            starts.Clear();
            await WaitUntilAsync(() => machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(Enumerable.Repeat(MotionGroup.PcbPlacementHandler, 3), starts.Take(3));
            foreach (var motion in motions.Values)
                Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).Homed));
            Assert.False(state.IsHoming);
            Assert.False(operations.HasActiveOperations);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            placement.StateChanged -= StopAfterPlacementHome;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SynchronousHomeStartFailureWaitsForAlreadyStartedAxes()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.BoltFastening = true;
        var failure = new MotionException("Start fastening HOME", new InvalidOperationException("SDK start failed."));
        var results = new Dictionary<MotionGroup, HomeResultMotion>();
        IXyMotion Wrap(IServiceProvider provider, MotionGroup group)
        {
            var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
            var result = (HomeResultMotion)motion;
            result.Motion = provider.GetRequiredKeyedService<IXyMotion>(group);
            result.AwaitCleanupAfterCancellation = true;
            if (group == MotionGroup.BoltFastening)
                result.StartFailure = failure;
            results.Add(group, result);
            return motion;
        }

        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings)
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
                Enum.GetValues<MotionGroup>().ToDictionary(group => group,
                    group => group is MotionGroup.PcbSupply or MotionGroup.BoltFastening
                        ? Wrap(provider, group) : provider.GetRequiredKeyedService<IXyMotion>(group)))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var homing = machine.HomeAsync(default);
        var supply = results[MotionGroup.PcbSupply];
        try
        {
            await results[MotionGroup.BoltFastening].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => supply.HomeCancellation.IsCancellationRequested);
            Assert.False(homing.IsCompleted);
            Assert.True(state.IsHoming);
            Assert.True(services.GetRequiredService<OperationCancellation>().HasActiveOperations);

            supply.Result.SetCanceled();
            await homing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("SDK start failed.", state.AlarmDetail);
            Assert.False(state.IsHoming);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.All(results.Values, result => Assert.Equal(0, result.HorizontalHomeCalls));
        }
        finally
        {
            supply.Result.TrySetCanceled();
            machine.Stop();
            await homing;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(HomeCommandTarget.Axis)]
    [InlineData(HomeCommandTarget.TeachingUnit)]
    [InlineData(HomeCommandTarget.AllUnits)]
    public async Task HomeCommandKeepsDispatcherResponsiveDuringSlowHardwareCall(HomeCommandTarget target)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var settings = FlowSettings();
                    settings.Units = EnableOnly(MachineUnit.Inspection);
                    await using var services = CreateDisplayServices(out var feedback, settings);
                    var machine = services.GetRequiredService<MachineController>();
                    using var release = new ManualResetEventSlim();
                    var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var homing = Task.CompletedTask;
                    await machine.InitializeAsync();
                    try
                    {
                        var uiThread = Environment.CurrentManagedThreadId;
                        IAsyncRelayCommand command;
                        if (target == HomeCommandTarget.AllUnits)
                        {
                            command = services.GetRequiredService<OperationViewModel>().HomeCommand;
                        }
                        else if (target == HomeCommandTarget.TeachingUnit)
                        {
                            var teaching = services.GetRequiredService<TeachingViewModel>();
                            teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
                            command = teaching.HomeCommand;
                        }
                        else
                        {
                            var manual = services.GetRequiredService<MotionDiagnosticsViewModel>();
                            var axis = manual.Axes.Single(
                                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
                            command = axis.HomeCommand;
                        }

                        await WaitUntilAsync(() => command.CanExecute(null));
                        var button = new Button();
                        button.SetBinding(Button.CommandProperty, new Binding { Source = command });
                        Assert.True(button.IsEnabled);
                        feedback.BeforeRead = () => Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                        feedback.BeforePositionRead = feedback.BeforeRead;
                        feedback.BeforeHome = () =>
                        {
                            entered.TrySetResult(Environment.CurrentManagedThreadId);
                            if (!release.Wait(TimeSpan.FromSeconds(5)))
                                throw new TimeoutException("The UI could not release the simulated hardware call.");
                        };

                        homing = command.ExecuteAsync(null);
                        var hardwareThread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                        Assert.NotEqual(Environment.CurrentManagedThreadId, hardwareThread);
                        Assert.False(homing.IsCompleted);
                        command.NotifyCanExecuteChanged();
                        Assert.False(button.IsEnabled);
                        var responded = false;
                        await dispatcher.InvokeAsync(() => responded = true, DispatcherPriority.Input);
                        Assert.True(responded);

                        release.Set();
                        await homing.WaitAsync(TimeSpan.FromSeconds(2));
                        feedback.BeforeRead = null;
                        feedback.BeforePositionRead = null;
                        Assert.True(feedback.Motion.GetAxisState(MotionAxis.X).Homed);
                        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
                    }
                    finally
                    {
                        release.Set();
                        feedback.BeforeHome = null;
                        feedback.BeforeRead = null;
                        feedback.BeforePositionRead = null;
                        await homing;
                        await machine.ShutdownAsync();
                    }
                    finished.TrySetResult();
                }
                catch (Exception exception)
                {
                    finished.TrySetException(exception);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task MotionWindowCloseCancelsAndAwaitsItsAxisHome()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        var monitor = services.GetRequiredService<MotionDiagnosticsViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await motion.MoveAxisAsync(MotionAxis.X, 20, 10_000);
            settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 1;
            var axis = monitor.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => axis.IsHomeAllowed);
            var homing = axis.HomeCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => motion.IsMoving);

            Assert.True(await monitor.TryCloseAsync());

            Assert.True(homing.IsCompletedSuccessfully);
            Assert.False(motion.IsMoving);
            Assert.False(motion.GetAxisState(MotionAxis.X).Homed);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    public enum HomeCommandTarget
    {
        Axis,
        TeachingUnit,
        AllUnits,
    }

    [Fact]
    public async Task HomeAdmissionAllowsPreparationButAxisMotionRequiresRaisedCylinders()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<MotionDiagnosticsViewModel>();
        await machine.InitializeAsync();
        Assert.True(machine.IsHomeAllowed);
        Assert.False(state.Ready);
        Assert.True(state.ManualSetupEnabled);
        io.SetInput(InputIo.AutoMode, false);
        Assert.False(state.ManualSetupEnabled);
        io.SetInput(InputIo.AutoMode, true);
        using (services.GetRequiredService<OperationCancellation>().Link())
            Assert.False(state.ManualSetupEnabled);
        Assert.True(state.ManualSetupEnabled);
        var outputsChanged = 0;
        io.OutputChanged += (_, _) => outputsChanged++;

        foreach (var (up, down, reason) in new[]
        {
            (
                InputIo.PcbPlacementHandlerUp,
                InputIo.PcbPlacementHandlerDown,
                HomeBlockReason.PlacementNotRaised),
            (InputIo.PcbPlacementIpmUp, InputIo.PcbPlacementIpmDown, HomeBlockReason.PlacementNotRaised),
            (InputIo.PickupHeadUp, InputIo.PickupHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.ShootingHeadUp, InputIo.ShootingHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.NgCarrierPickupUp, InputIo.NgCarrierPickupDown, HomeBlockReason.NgPickupNotRaised),
        })
        {
            io.SetInput(up, false);
            Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
            Assert.Equal(reason, machine.GetHomeBlock(requireRaised: true));
            Assert.True(machine.IsHomeAllowed);
            io.SetInput(down, true);
            io.SetInput(up, true);
            Assert.Equal(reason, machine.GetHomeBlock(requireRaised: true));
            io.SetInput(down, false);
            Assert.True(machine.IsHomeAllowed);
        }

        Assert.Equal(0, outputsChanged);

        settings.Units.PcbSupply = settings.Units.PcbPlacement = settings.Units.BoltFastening = false;
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.PickupHeadUp, false);
        io.SetInput(InputIo.NgShuttleUp, false);
        io.SetInput(InputIo.NgShuttleDown, true);
        Assert.True(machine.IsHomeAllowed);
        services.GetRequiredService<MachineState>().Refresh();
        await WaitUntilAsync(() => manual.Axes[9].IsHomeAllowed);
        Assert.False(manual.Axes[3].IsHomeAllowed);
        Assert.True(manual.Axes[9].IsHomeAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeCanRepeatAndIgnoresCarrierInputs(bool individualAxis)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var manual = services.GetRequiredService<MotionDiagnosticsViewModel>();
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
            Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
            Assert.True(machine.IsHomeAllowed);
            var axis = manual.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => axis.IsHomeAllowed);
            var moved = false;
            gantry.Motion.Feedback.MovingChanged += moving =>
            {
                if (moving)
                {
                    moved = true;
                    io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                    io.SetInput(InputIo.NgCarrierDetected, true);
                }
            };

            for (var run = 0; run < 2; run++)
            {
                moved = false;
                Assert.True(machine.IsHomeAllowed);
                if (individualAxis)
                    await axis.HomeCommand.ExecuteAsync(null);
                else
                    await machine.HomeAsync(CancellationToken.None);

                Assert.True(moved);
                Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
                Assert.True(io.GetInput(InputIo.NgCarrierDetected));
                Assert.True(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                Assert.False(state.IsHoming);
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task BoltHomeIgnoresStationaryNgPickupState()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.Inspection = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        try
        {
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
            io.SetInput(InputIo.NgCarrierDetected, true);
            var ngMoved = false;
            gantry.Motion.Feedback.MovingChanged += moving => ngMoved |= moving;
            teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
            await WaitUntilAsync(() => teaching.IsHomeAllowed);

            await teaching.HomeCommand.ExecuteAsync(null);

            var fastening = services.GetRequiredService<BoltFasteningStation>();
            Assert.All(fastening.Motion.Feedback.Axes, axis => Assert.True(fastening.Motion.Feedback.GetAxisState(axis).Homed));
            Assert.False(ngMoved);
            Assert.True(io.GetInput(InputIo.NgCarrierPickupDown));
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsHoming);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task HomeRaisesCylindersBeforeMovingAxesAndDisplaysHoming()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        IIoService signals = io;
        await machine.InitializeAsync();
        OutputIo[] cylinders = [
            OutputIo.PcbPlacementHandlerDown,
            OutputIo.PcbPlacementIpmDown,
            OutputIo.PickupHeadDown,
            OutputIo.ShootingHeadDown,
            OutputIo.NgCarrierPickupDown,
        ];
        await Task.WhenAll(cylinders.Select(
            output => signals.SetOutputAndWaitAsync(output, true)));
        var motions = new[]
        {
            services.GetRequiredService<PcbSupplier>().Motion.Feedback,
            services.GetRequiredService<PcbPlacer>().Motion.Feedback,
            services.GetRequiredService<BoltFasteningStation>().Motion.Feedback,
            services.GetRequiredService<InspectionStation>().Motion.Feedback,
        };
        var moved = false;
        var movedBeforeRaised = false;
        foreach (var motion in motions)
            motion.MovingChanged += moving =>
            {
                moved |= moving;
                movedBeforeRaised |= moving && machine.GetHomeBlock(requireRaised: true) != HomeBlockReason.None;
            };
        var outputChanges = new ConcurrentQueue<OutputIo>();
        io.OutputChanged += (output, _) => outputChanges.Enqueue(output);

        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.True(machine.IsHomeAllowed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.False(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        Assert.Empty(outputChanges);
        await WaitUntilAsync(() => !machine.IsHomeAllowed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, false);
        Assert.True(machine.IsHomeAllowed);
        await WaitUntilAsync(() => machine.IsHomeAllowed);

        var homing = machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.IsHoming);
        Assert.True(state.IsRunning);
        Assert.True(state.IsHoming);
        Assert.Equal(MachineDisplayState.Homing, services.GetRequiredService<OperationViewModel>().MachineDisplayState);
        Assert.False(state.ManualSetupEnabled);
        Assert.False(machine.IsHomeAllowed);
        await homing;
        await WaitUntilAsync(() => machine.IsHomeAllowed && state.FeedbackReadiness.Homed);
        Assert.True(machine.IsHomeAllowed);
        Assert.True(state.FeedbackReadiness.Homed);
        Assert.False(state.IsHoming);
        Assert.True(moved);
        Assert.False(movedBeforeRaised);
        Assert.Equal(cylinders.Order(), outputChanges.Order());
        Assert.All(cylinders, output => Assert.False(io.GetOutput(output)));
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmUp));
        Assert.False(io.GetInput(InputIo.PcbPlacementIpmDown));
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(machine.IsHomeAllowed);
    }

    [Fact]
    public async Task HomePreparationStopsBeforeLiftingIpmWhenPlacementDetectsPcb()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        IIoService signals = io;
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, true);
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        void DetectPcb(OutputIo output, bool on)
        {
            if (output == OutputIo.PcbPlacementHandlerDown && !on)
                io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        }

        io.OutputChanged += DetectPcb;
        try
        {
            Assert.True(machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None);
            Assert.True(io.GetInput(InputIo.PcbPlacementPcbDetected));
            Assert.True(io.GetOutput(OutputIo.PcbPlacementIpmDown));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
            Assert.False(machine.IsHomeAllowed);
        }
        finally
        {
            io.OutputChanged -= DetectPcb;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeAndStartPreparationStopBeforeAxesOnStopOrTimeout(bool start)
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.Inspection),
        };
        FastHomes(settings);
        settings.Options.TimeoutMilliseconds = 500;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        if (start)
            await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.NgCarrierPickupDown, true);
        io.SetOutput(OutputIo.PcbPlacementHandlerDown, true);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        var operation = services.GetRequiredService<OperationViewModel>();
        // This covers device preparation after the UI confirmation, not the modal review.
        var running = start ? machine.StartAsync() : operation.HomeCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => state.IsRunning && !io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.Equal(!start, state.IsHoming);
        Assert.False(state.AutomaticRunning);
        Assert.True(operation.StopCommand.CanExecute(null));
        await operation.StopCommand.ExecuteAsync(null);
        await running;
        Assert.False(state.IsHoming);
        Assert.Equal(start, state.FeedbackReadiness.Homed);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementHandlerDown));
        Assert.True(machine.IsHomeAllowed);

        if (start)
            await machine.StartAsync();
        else
            await operation.HomeCommand.ExecuteAsync(null);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.False(state.IsRunning);
        Assert.Equal(start, state.FeedbackReadiness.Homed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomePreparationFailureAfterStopIsReportedWithoutReplacingSafetyAlarm(bool safetyStop)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.Inspection) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var failure = new IOException("Cylinder output failed after STOP.");
        await machine.InitializeAsync();
        io.SetOutput(OutputIo.NgCarrierPickupDown, true);
        void FailAfterStop(OutputIo output, bool on)
        {
            if (output != OutputIo.NgCarrierPickupDown || on)
                return;
            io.OutputChanged -= FailAfterStop;
            if (safetyStop)
                io.SetInput(InputIo.AirPressureHigh, false);
            else
                machine.Stop();
            throw failure;
        }

        io.OutputChanged += FailAfterStop;
        try
        {
            Assert.True(machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None);

            Assert.Equal(
                safetyStop ? MachineAlarm.AirPressureLow : MachineAlarm.NgCarrierTransfer,
                state.Alarm);
            if (safetyStop)
                Assert.Null(state.AlarmDetail);
            else
                Assert.Equal(failure.ToString(), state.AlarmDetail);
            var entry = Assert.Single(
                services.GetRequiredService<ApplicationLog>().Snapshot(),
                entry => entry.Detail == failure.ToString());
            Assert.Contains(nameof(MachineAlarm.NgCarrierTransfer), entry.Message);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.False(state.IsRunning);
        }
        finally
        {
            io.OutputChanged -= FailAfterStop;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionHomeRaisesPickupAndIgnoresCarrierInput()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.Inspection),
        };
        FastHomes(settings);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        IIoService signals = io;
        await machine.InitializeAsync();
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, true);
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.True(machine.IsHomeAllowed);
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.HomeAxisAsync(MotionAxis.X));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.False(state.FeedbackReadiness.Homed);

        var transfer = services.GetRequiredService<InspectionStation>();
        Assert.False(transfer.IsRaised);
        Assert.True(io.GetOutput(OutputIo.NgCarrierPickupDown));

        var unsafeMovement = false;
        gantry.Motion.Feedback.MovingChanged += moving =>
        {
            if (moving && state.IsHoming)
            {
                unsafeMovement |= !transfer.IsRaised;
            }
        };
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.FeedbackReadiness.Homed);
        Assert.False(unsafeMovement);
        Assert.True(transfer.IsRaised);
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));

        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.JogAsync(MotionAxis.X, 10));
        await Assert.ThrowsAsync<MotionInterlockException>(
            async () => await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 100));
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        var moving = gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 10);
        await WaitUntilAsync(() => gantry.Motion.Feedback.IsMoving);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.Contains("pickup Up", state.AlarmDetail);
        Assert.Contains("Current lift: Between", state.AlarmDetail);

        io.SetInput(InputIo.NgCarrierPickupUp, true);
        await machine.ResetAsync();
        await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 1000);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(InputIo.PickupHeadUp, false, false)]
    [InlineData(InputIo.PcbPlacementIpmUp, false, false)]
    [InlineData(InputIo.PcbPlacementIpmUp, false, true)]
    public async Task HomeStopsWhenItsCylinderConditionChanges(
        InputIo input,
        bool value,
        bool teachingHome)
    {
        var settings = FlowSettings();
        foreach (var (motionSettings, _) in settings.MotionSections)
            motionSettings.ZHome.SearchSpeed = 20;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var fastening = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await Task.WhenAll(
            services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler)
                .MoveAxisAsync(MotionAxis.Z, 50, 10_000),
            services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening)
                .MoveAxisAsync(MotionAxis.Z, 50, 10_000));
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await WaitUntilAsync(() => teaching.IsHomeAllowed);
        var homing = teachingHome
            ? teaching.HomeCommand.ExecuteAsync(null)
            : machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => placement.Motion.Feedback.IsMoving);
        Assert.False(fastening.Motion.Feedback.IsMoving);
        Assert.False(state.ManualSetupEnabled);
        io.SetInput(input, value);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsHoming);
        Assert.False(placement.Motion.Feedback.IsMoving);
        Assert.False(fastening.Motion.Feedback.IsMoving);
        Assert.False(placement.Motion.Feedback.GetAxisState(MotionAxis.Z).Homed);
        Assert.Equal(50, fastening.Motion.Feedback.Position.Z);
        Assert.NotEqual(HomeBlockReason.None, machine.GetHomeBlock(requireRaised: true));
        await WaitUntilAsync(() => machine.IsHomeAllowed);
        if (teachingHome)
        {
            await WaitUntilAsync(() => teaching.IsHomeAllowed);
        }
    }

    [Fact]
    public async Task MotionAlarmBlocksHomeAndStopsAllHomingAxes()
    {
        var settings = FlowSettings();
        foreach (var (motionSettings, _) in settings.MotionSections)
            motionSettings.ZHome.SearchSpeed = 20;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var supply = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.PcbSupply);
        var fastening = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.BoltFastening);
        await machine.InitializeAsync();

        fastening.SetAlarm(MotionAxis.X, true);
        await WaitUntilAsync(() => !machine.IsHomeAllowed);
        await WaitUntilAsync(() => machine.IsResetAllowed);
        await machine.ResetAsync();
        Assert.True(machine.IsHomeAllowed);

        await Task.WhenAll(
            supply.MoveAxisAsync(MotionAxis.Z, 50, 10_000),
            fastening.MoveAxisAsync(MotionAxis.Z, 50, 10_000));
        // The command can finish before the motion scan publishes stopped feedback.
        await WaitUntilAsync(() => machine.IsHomeAllowed);
        var homing = machine.HomeAsync(CancellationToken.None);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(
            () => supply.IsMoving && fastening.IsMoving, TimeSpan.FromSeconds(2)),
            $"Home completed={homing.IsCompleted}, IsHomeAllowed={machine.IsHomeAllowed}, "
                + $"block={machine.HomeBlock}, alarm={state.Alarm}, detail={state.AlarmDetail}");
        fastening.SetAlarm(MotionAxis.X, true);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.IsHoming);
        Assert.False(state.IsRunning);
        Assert.False(supply.IsMoving);
        Assert.False(fastening.IsMoving);
        Assert.False(supply.GetAxisState(MotionAxis.Z).Homed);
        Assert.False(fastening.GetAxisState(MotionAxis.Z).Homed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelHomeReportsEachUnitsFailureAfterTheFirstFailureStopsHome(bool unexpectedCancellation)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.BoltFastening = true;
        var results = new Dictionary<MotionGroup, HomeResultMotion>();
        IXyMotion Wrap(IServiceProvider provider, MotionGroup group)
        {
            var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
            var result = (HomeResultMotion)motion;
            result.Motion = provider.GetRequiredKeyedService<IXyMotion>(group);
            result.AwaitCleanupAfterCancellation = true;
            results.Add(group, result);
            return motion;
        }

        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings)
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
                Enum.GetValues<MotionGroup>().ToDictionary(group => group,
                    group => group is MotionGroup.PcbSupply or MotionGroup.BoltFastening
                        ? Wrap(provider, group) : provider.GetRequiredKeyedService<IXyMotion>(group)))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var log = services.GetRequiredService<ApplicationLog>();
        await machine.InitializeAsync();
        var homing = machine.HomeAsync(default);
        Exception firstFailure = unexpectedCancellation
            ? new OperationCanceledException("Supply home failed without a STOP request.")
            : new MotionException("Supply home", new IOException("Supply home failed."));
        var stopFailure = new MotionException("Fastening STOP", new IOException("Fastening stop failed."));
        try
        {
            await Task.WhenAll(results.Values.Select(result => result.Started.Task))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(state.IsHoming);
            results[MotionGroup.PcbSupply].Result.SetException(firstFailure);
            await WaitUntilAsync(() => results[MotionGroup.BoltFastening].HomeCancellation.IsCancellationRequested);
            Assert.False(homing.IsCompleted);
            results[MotionGroup.BoltFastening].Result.SetException(stopFailure);
            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains("Supply home failed", state.AlarmDetail);
            Assert.Contains(log.Snapshot(), entry => entry.Detail?.Contains("Fastening stop failed.") == true);
            Assert.False(state.IsHoming);
            Assert.All(results.Values, result => Assert.Equal(0, result.HorizontalHomeCalls));
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            foreach (var result in results.Values)
                result.Result.TrySetCanceled();
            machine.Stop();
            await homing;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    public async Task FailedHomeReportsCauseAndStopsOtherHomingAxes(
        bool exception,
        bool individual,
        bool teachingHome,
        bool safetyStop)
    {
        var settings = FlowSettings();
        foreach (var (motionSettings, _) in settings.MotionSections)
            motionSettings.ZHome.SearchSpeed = 1;
        HomeResultMotion? homeResult = null;
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings)
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
            {
                var motions = Enum.GetValues<MotionGroup>().ToDictionary(
                    group => group, group => provider.GetRequiredKeyedService<IXyMotion>(group));
                var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
                homeResult = (HomeResultMotion)motion;
                homeResult.AwaitCleanupAfterCancellation = safetyStop;
                homeResult.Motion = motions[MotionGroup.BoltFastening];
                motions[MotionGroup.BoltFastening] = motion;
                return motions;
            })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        var supply = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        var manual = services.GetRequiredService<MotionDiagnosticsViewModel>();
        await machine.InitializeAsync();
        await supply.MoveAxisAsync(MotionAxis.Z, 50, 10_000);

        var axisRow = manual.Axes.Single(
            row => row.Group == MotionGroup.BoltFastening && row.Axis == MotionAxis.Z);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        var homing = teachingHome
            ? teaching.HomeCommand.ExecuteAsync(null)
            : individual
                ? axisRow.HomeCommand.ExecuteAsync(null)
                : machine.HomeAsync(CancellationToken.None);
        try
        {
            await homeResult!.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => individual || teachingHome ? state.IsHoming : supply.IsMoving);
            if (safetyStop)
            {
                services.GetRequiredService<VirtualIoService>().SetInput(InputIo.EmergencyStop1Pressed, true);
            }
            if (exception)
            {
                homeResult!.Result.SetException(
                    new MotionException("Home", new InvalidOperationException("Home command failed.")));
            }
            else
            {
                homeResult!.Result.SetResult(false);
            }

            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            var expectedAlarm = safetyStop ? MachineAlarm.EmergencyStop : MachineAlarm.HomeFailed;
            Assert.Equal(expectedAlarm, state.Alarm);
            if (exception)
                Assert.Contains("Home command failed.", state.AlarmDetail);
            await WaitUntilAsync(() => state.Alarm == expectedAlarm && !state.IsRunning);
            // A latched home failure does not block a retry while the axis feedback remains healthy.
            Assert.Equal(!safetyStop, axisRow.IsHomeAllowed);
            Assert.False(state.IsHoming);
            Assert.False(placement.IsMoving);
            Assert.False(supply.IsMoving);
            Assert.Equal(!individual && !teachingHome, placement.GetAxisState(MotionAxis.Z).Homed);
            Assert.False(supply.GetAxisState(MotionAxis.Z).Homed);
            Assert.Equal(0, homeResult.HorizontalHomeCalls);
        }
        finally
        {
            axisRow.HomeCommand.Cancel();
            teaching.HomeCommand.Cancel();
            machine.Stop();
            await homing;
        }
    }

    public class HomeResultMotion : DispatchProxy
    {
        public HomeResultMotion()
        {
            Result = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IXyMotion Motion { get; set; } = null!;
        public TaskCompletionSource<bool> Result { get; }
        public int HorizontalHomeCalls { get; private set; }
        public bool AwaitCleanupAfterCancellation { get; set; }
        public Exception? StartFailure { get; set; }
        public TaskCompletionSource Started { get; }
        public CancellationToken HomeCancellation { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IAxisMotion.HomeAsync)
                && (MotionAxis)arguments![0]! == MotionAxis.Z)
            {
                HomeCancellation = (CancellationToken)arguments[2]!;
                Started.TrySetResult();
                if (StartFailure is not null)
                    throw StartFailure;
                return AwaitCleanupAfterCancellation
                    ? Result.Task
                    : Result.Task.WaitAsync(HomeCancellation);
            }

            if (method.Name == nameof(IXyMotion.HomeHorizontalAsync))
            {
                HorizontalHomeCalls++;
            }

            return method.Invoke(Motion, arguments);
        }
    }
}
