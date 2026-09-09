using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

// A plain WPF Application supplies resources/Dispatcher only. Never start IBTM.App.
[CollectionDefinition("Output window UI", DisableParallelization = true)]
public sealed class OutputWindowUiCollection;

[Collection("Output window UI")]
public sealed class OutputWindowThreadingTests
{
    [Fact]
    public async Task BoundConveyorButtonsKeepDisplayAliveAcrossOffCloseAndReopen()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Exception? failure = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyWindowsAsync(app); }
                catch (Exception error) { failure = error; }
                finally
                {
                    app.Shutdown();
                }
            }));
            app.Run();
            if (failure is null) finished.TrySetResult();
            else finished.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task VerifyWindowsAsync(Application app)
    {
        foreach (var resource in new[] { "AppStyles", "IoWindowStyles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/IBTM;component/UI/{resource}.xaml"),
            });
        using var services = new ServiceCollection()
            .AddSingleton(new MachineStore(Path.Combine(Path.GetTempPath(), $"IBTM-output-ui-{Guid.NewGuid():N}.db")))
            .AddIbtmApplication(new MachineSettings
            {
                Drivers = new() { Inspection = InspectionAlgorithm.Virtual },
                Units = new()
                {
                    MainConveyor = true, NgConveyor = true,
                    PcbSupply = false, PcbPlacement = false, BoltFastening = false,
                    Inspection = false, NgCarrierTransfer = false, NgShuttle = false,
                    PickupBoltFeeder = false, ShootingBoltFeeder = false,
                },
            }).BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        var main = services.GetRequiredService<MainViewModel>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        var log = services.GetRequiredService<ApplicationLog>();
        var notifications = new List<int>();
        var uiThread = Environment.CurrentManagedThreadId;
        var openButton = new Button();
        openButton.SetBinding(UIElement.IsEnabledProperty,
            new Binding(nameof(MainViewModel.OutputsWindowEnabled)) { Source = main });
        OutputWindow? window = null;
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            manual.Activate();
            foreach (var output in new[] { OutputIo.MainConveyorRun, OutputIo.NgConveyorRun })
            {
                for (var reopen = 0; reopen < 2; reopen++)
                {
                    Assert.True(openButton.IsEnabled);
                    window = new OutputWindow(signals, machine, state);
                    var row = window.Rows.Single(candidate => candidate.Io.Signal == output);
                    var manualRow = manual.Conveyors.Single(candidate => candidate.Io.Signal == output);
                    // These are real WPF command subscribers with the production bindings.
                    var outputButton = BoundButton(row, nameof(OutputControlRow.ActionCommand));
                    var manualStop = BoundButton(manualRow, nameof(OutputControlRow.StopOutputTestCommand));
                    row.StopOutputTestCommand.CanExecuteChanged += (_, _) => notifications.Add(Environment.CurrentManagedThreadId);
                    row.PropertyChanged += (_, _) => notifications.Add(Environment.CurrentManagedThreadId);
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.Closed += (_, _) => closed.TrySetResult();
                    Assert.True(await VirtualTest.WaitUntilAsync(() => outputButton.IsEnabled, TimeSpan.FromSeconds(2)));

                    outputButton.Command.Execute(null);
                    var run = row.ToggleCommand.ExecutionTask!;
                    Assert.True(await VirtualTest.WaitUntilAsync(() => io.GetOutput(output)
                        && (string?)outputButton.Content == "OFF" && manualStop.IsEnabled, TimeSpan.FromSeconds(2)));
                    Assert.True(openButton.IsEnabled);

                    if (reopen == 0) outputButton.Command.Execute(null);
                    else manualStop.Command.Execute(null);
                    await run.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.True(await VirtualTest.WaitUntilAsync(() => !io.GetOutput(output)
                        && (string?)outputButton.Content == "ON" && outputButton.IsEnabled
                        && !manualStop.IsEnabled, TimeSpan.FromSeconds(2)));

                    // Also exercise background DI/DO notifications after OFF. Neither may
                    // invoke a command subscriber or stop the common display worker.
                    await Task.Run(() => io.SetOutput(OutputIo.MachineLight, !io.GetOutput(OutputIo.MachineLight)));
                    var feedback = new TextBlock();
                    feedback.SetBinding(TextBlock.TextProperty,
                        new Binding(nameof(OutputControlRow.FeedbackState))
                        {
                            Source = window.Rows.Single(candidate => candidate.Io.Signal == OutputIo.NgConveyorStopperUp),
                        });
                    await Task.Run(() =>
                    {
                        io.SetInput(InputIo.NgConveyorStopperUp, true);
                        io.SetInput(InputIo.NgConveyorStopperDown, true);
                    });
                    Assert.True(await VirtualTest.WaitUntilAsync(() => feedback.Text == nameof(OutputFeedbackState.Conflict),
                        TimeSpan.FromSeconds(2)));
                    await Task.Run(() => io.SetInput(InputIo.NgConveyorStopperUp, false));
                    var previous = state.Display;
                    state.RequestDisplayRefresh();
                    Assert.True(await VirtualTest.WaitUntilAsync(() => !ReferenceEquals(previous, state.Display), TimeSpan.FromSeconds(2)));
                    Assert.True(state.Display.Available);
                    Assert.True(openButton.IsEnabled);
                    Assert.False(closed.Task.IsCompleted); // OFF must not close OUTPUTS.
                    window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    window = null;
                }
            }
            Assert.NotEmpty(notifications);
            Assert.All(notifications, id => Assert.Equal(uiThread, id));
            Assert.DoesNotContain(log.ReadAfter(0), entry => entry.Message.Contains("Display worker stopped"));
        }
        finally
        {
            if (window is not null) { await window.ShutdownAsync(); window.Close(); }
            await main.ShutdownAsync();
            await machine.ShutdownAsync();
        }
    }

    private static Button BoundButton(OutputControlRow row, string command)
    {
        var button = new Button();
        button.SetBinding(Button.CommandProperty, new Binding(command) { Source = row });
        button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(OutputControlRow.ToggleLabel)) { Source = row });
        return button;
    }
}
