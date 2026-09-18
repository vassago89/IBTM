using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeCommandKeepsDispatcherResponsiveDuringSlowHardwareCall(bool teachingHome)
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
                    settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
                    using var services = CreateDisplayServices(out var feedback, settings);
                    var machine = services.GetRequiredService<MachineController>();
                    using var release = new ManualResetEventSlim();
                    var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var homing = Task.CompletedTask;
                    await machine.InitializeAsync();
                    try
                    {
                        IAsyncRelayCommand command;
                        if (teachingHome)
                        {
                            var teaching = services.GetRequiredService<TeachingViewModel>();
                            teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;
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
                        var responded = false;
                        await dispatcher.InvokeAsync(() => responded = true, DispatcherPriority.Input);
                        Assert.True(responded);

                        release.Set();
                        await homing.WaitAsync(TimeSpan.FromSeconds(2));
                        Assert.True(feedback.Motion.GetAxisState(MotionAxis.X).Homed);
                        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
                    }
                    finally
                    {
                        release.Set();
                        feedback.BeforeHome = null;
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
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task MotionWindowCloseCancelsAndAwaitsItsAxisHome()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
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
}
