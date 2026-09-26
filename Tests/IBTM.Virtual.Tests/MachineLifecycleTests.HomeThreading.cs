using IBTM.Storage;
using IBTM.BoltFeeder;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.BoltFastening;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
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
            await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));

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

    [Fact]
    public async Task AllUnitsHomeFinishesPlacementFirstAndEndsWithoutMovingToWorkHeights()
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.HandoffPosition.Z = 8;
        settings.BoltFastening.SafeZ = 12;
        settings.PcbSupply.RotationZ = 16;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var outputs = new ConcurrentQueue<OutputIo>();
        services.GetRequiredService<VirtualIoService>().OutputChanged += (output, _) => outputs.Enqueue(output);
        var motions = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>();
        var placement = motions[MotionGroup.PcbPlacementHandler];
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
            await machine.HomeAsync(default);

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Empty(outputs);
            Assert.Equal(motions.Count - 1, starts.Select(start => start.Group).Distinct().Count());
            Assert.All(starts, start => Assert.True(start.PlacementHomed, $"{start.Group} started before Placement HOME completed."));
            foreach (var motion in motions.Values)
            {
                Assert.Equal((0, 0, 0), motion.Position);
                Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).Homed));
            }
            Assert.False(state.IsHoming);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StopAtPlacementHomeCompletionPreventsRemainingHomeAndAllowsRestart()
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
            if (!placement.Axes.All(axis => placement.GetAxisState(axis).Homed))
                return;
            placement.StateChanged -= StopAfterPlacementHome;
            machine.Stop();
        }

        placement.StateChanged += StopAfterPlacementHome;
        try
        {
            await machine.HomeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(new[] { MotionGroup.PcbPlacementHandler, MotionGroup.PcbPlacementHandler }, starts);
            Assert.False(state.IsHoming);
            Assert.False(operations.HasActiveOperations);
            Assert.Equal(MachineAlarm.None, state.Alarm);

            starts.Clear();
            await WaitUntilAsync(() => machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(new[] { MotionGroup.PcbPlacementHandler, MotionGroup.PcbPlacementHandler }, starts.Take(2));
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

        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
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
                            var manual = services.GetRequiredService<MotionWindowViewModel>();
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
        var monitor = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await motion.MoveAxisAsync(MotionAxis.X, 20, 10_000);
            settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 1;
            var axis = monitor.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => axis.HomeCommand.CanExecute(null));
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
}
