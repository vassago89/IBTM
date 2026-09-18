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
                        object? parameter = null;
                        if (teachingHome)
                        {
                            var teaching = services.GetRequiredService<TeachingViewModel>();
                            teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;
                            command = teaching.HomeCommand;
                        }
                        else
                        {
                            var manual = services.GetRequiredService<MotionWindowViewModel>();
                            parameter = manual.Axes.Single(
                                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
                            command = manual.HomeAxisCommand;
                        }

                        await WaitUntilAsync(() => command.CanExecute(parameter));
                        feedback.BeforeHome = () =>
                        {
                            entered.TrySetResult(Environment.CurrentManagedThreadId);
                            if (!release.Wait(TimeSpan.FromSeconds(5)))
                                throw new TimeoutException("The UI could not release the simulated hardware call.");
                        };

                        homing = command.ExecuteAsync(parameter);
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
}
