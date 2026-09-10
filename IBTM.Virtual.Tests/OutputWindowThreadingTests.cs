using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
        var thread = new Thread(
            () =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Exception? failure = null;
                app.DispatcherUnhandledException += (_, args) =>
                {
                    failure = args.Exception;
                    args.Handled = true;
                    app.Shutdown();
                };
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));
                _ = dispatcher.BeginInvoke(
                    new Action(
                        async () =>
                        {
                            try
                            {
                                await VerifyWindowsAsync(app);
                            }
                            catch (Exception error)
                            {
                                failure = error;
                            }
                            finally
                            {
                                app.Shutdown();
                            }
                        }));
                app.Run();
                if (failure is null)
                    finished.TrySetResult();
                else
                    finished.TrySetException(failure);
            })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task VerifyWindowsAsync(Application app)
    {
        foreach (var resource in new[] { "AppStyles", "IoWindowStyles" })
            app.Resources.MergedDictionaries.Add(
                new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/IBTM;component/UI/{resource}.xaml"),
                });
        await VerifyLogBindingsAsync();
        using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(
                new MachineSettings
                {
                    Drivers = new() { Inspection = InspectionAlgorithm.Virtual },
                    Units = new()
                    {
                        MainConveyor = true,
                        NgConveyor = true,
                        PcbSupply = false,
                        PcbPlacement = false,
                        BoltFastening = false,
                        Inspection = false,
                        NgCarrierTransfer = false,
                        NgShuttle = false,
                        PickupBoltFeeder = false,
                        ShootingBoltFeeder = false,
                    },
                })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        await machine.InitializeAsync();
        var main = services.GetRequiredService<MainViewModel>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        var log = services.GetRequiredService<ApplicationLog>();
        var notifications = new List<int>();
        var uiThread = Environment.CurrentManagedThreadId;
        var openButton = new Button();
        openButton.SetBinding(
            UIElement.IsEnabledProperty,
            new Binding(nameof(MainViewModel.OutputsWindowEnabled))
            { Source = main });
        OutputWindow? window = null;
        try
        {
            io.AutoResponseEnabled = false;
            await VerifyDirectBindingsAsync(services);
            await VerifyBackgroundDisplayBindingsAsync(services);
            manual.Activate();
            const OutputIo output = OutputIo.MainConveyorRun;
            for (var reopen = 0; reopen < 2; reopen++)
            {
                Assert.True(openButton.IsEnabled);
                window = new OutputWindow(signals, machine, state);
                var row = window.Rows.Single(candidate => candidate.Io.Signal == output);
                var manualRow = manual.Conveyors.Single(candidate => candidate.Io.Signal == output);
                // These are real WPF command subscribers with the production bindings.
                var outputButton = BoundButton(row);
                var manualStop = new Button { DataContext = manualRow };
                manualStop.SetBinding(
                    Button.CommandProperty,
                    new Binding(nameof(ManualConveyorRow.StopCommand)));
                row.ToggleCommand.CanExecuteChanged += (_, _) => notifications.Add(
                    Environment.CurrentManagedThreadId);
                row.PropertyChanged += (_, _) => notifications.Add(Environment.CurrentManagedThreadId);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => outputButton.IsEnabled,
                        TimeSpan.FromSeconds(2)));

                outputButton.Command.Execute(null);
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => io.GetOutput(output)
                            && (string?)outputButton.Content == "OFF"
                            && manualStop.IsEnabled,
                        TimeSpan.FromSeconds(2)));
                Assert.True(openButton.IsEnabled);

                if (reopen == 0)
                    outputButton.Command.Execute(null);
                else
                    manualStop.Command.Execute(null);
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => !io.GetOutput(output)
                            && (string?)outputButton.Content == "ON"
                            && outputButton.IsEnabled,
                        TimeSpan.FromSeconds(2)));
                Assert.True(manualStop.IsEnabled); // STOP is always callable, even after OFF.
                Assert.Same(row.ToggleCommand, outputButton.Command);
                // Also exercise background DI/DO notifications after OFF. Neither may
                // invoke a command subscriber or stop the common display worker.
                await Task.Run(
                    () => io.SetOutput(OutputIo.MachineLight, !io.GetOutput(OutputIo.MachineLight)));
                var feedback = BoundFeedback(
                    window.Rows.Single(candidate => candidate.Io.Signal == OutputIo.NgConveyorStopperUp));
                await Task.Run(
                    () =>
                    {
                        io.SetInput(InputIo.NgConveyorStopperUp, true);
                        io.SetInput(InputIo.NgConveyorStopperDown, true);
                    });
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => feedback.Text == "Input conflict",
                        TimeSpan.FromSeconds(2)));
                await Task.Run(() => io.SetInput(InputIo.NgConveyorStopperUp, false));
                var previous = state.Display;
                state.RequestDisplayRefresh();
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => !ReferenceEquals(previous, state.Display),
                        TimeSpan.FromSeconds(2)));
                Assert.True(state.Display.Available);
                Assert.True(openButton.IsEnabled);
                Assert.False(closed.Task.IsCompleted); // OFF must not close OUTPUTS.
                row.ToggleCommand.Execute(null);
                Assert.True(io.GetOutput(output));
                window.Close();
                Assert.True(closed.Task.IsCompleted);
                Assert.False(io.GetOutput(output));
                window = null;
            }

            Assert.All(notifications, id => Assert.Equal(uiThread, id));
            Assert.DoesNotContain(
                log.ReadAfter(0),
                entry => entry.Message.Contains("Display worker stopped"));
        }
        finally
        {
            if (window is not null)
            {
                window.Shutdown();
                window.Close();
            }

            await main.ShutdownAsync();
            await machine.ShutdownAsync();
        }
    }

    private static async Task VerifyBackgroundDisplayBindingsAsync(ServiceProvider services)
    {
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var input = new InputWindow(io, services.GetRequiredService<IoSignals>());
        var response = new CheckBox();
        response.SetBinding(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(InputWindow.AutoResponseEnabled)) { Source = input });
        try
        {
            await Task.Run(() => io.AutoResponseEnabled = true);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => response.IsChecked == true,
                TimeSpan.FromSeconds(2)));
            await Task.Run(() => io.AutoResponseEnabled = false);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => response.IsChecked == false,
                TimeSpan.FromSeconds(2)));
        }
        finally
        {
            input.Close();
        }

        var operation = services.GetRequiredService<OperationViewModel>();
        operation.Activate();
        var running = new CheckBox();
        running.SetBinding(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding("State.Display.ConveyorRunning") { Source = operation, Mode = BindingMode.OneWay });
        foreach (var on in new[] { true, false })
        {
            await Task.Run(() => io.SetOutput(OutputIo.MainConveyorRun, on));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => running.IsChecked == on,
                TimeSpan.FromSeconds(2)));
        }

        var bus = new VirtualAdcBus();
        bus.Open("Virtual", 19200);
        var adc = new AdcProtocolWindow(bus, new IBTM.Hantas.HantasSettings(), machine);
        var frames = (ListBox)adc.FindName("FrameLogList");
        var uiThread = Environment.CurrentManagedThreadId;
        var updates = new List<int>();
        ((INotifyCollectionChanged)frames.Items).CollectionChanged += (_, _) =>
            updates.Add(Environment.CurrentManagedThreadId);
        try
        {
            Assert.True(BindingOperations.IsDataBound(frames, ItemsControl.ItemsSourceProperty));
            await Task.Run(() => bus.ReadDeviceInformationAsync(0));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => frames.Items.Count == 2,
                TimeSpan.FromSeconds(2)));
            Assert.Contains("RX RAW", (string)frames.Items[0]);
            Assert.Contains("TX", (string)frames.Items[1]);
            Assert.All(updates, thread => Assert.Equal(uiThread, thread));
        }
        finally
        {
            adc.Close();
            bus.Close();
        }

        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        var preview = new Image();
        preview.SetBinding(
            Image.SourceProperty,
            new Binding(nameof(StationTeachingViewModel.LiveImage)) { Source = teaching });
        try
        {
            teaching.ToggleLiveViewCommand.Execute(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => preview.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true },
                TimeSpan.FromSeconds(2)));
            teaching.ToggleLiveViewCommand.Execute(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => preview.Source is null,
                TimeSpan.FromSeconds(2)));
        }
        finally
        {
            teaching.Deactivate();
        }
    }


    private static Button BoundButton(OutputWindowRow row)
    {
        var button = new Button { DataContext = row };
        button.Style = (Style)Application.Current.FindResource("OutputToggleButtonStyle");
        button.SetBinding(Button.CommandProperty, new Binding(nameof(OutputWindowRow.ToggleCommand)));
        return button;
    }

    private static async Task VerifyLogBindingsAsync()
    {
        using var log = new ApplicationLog();
        log.Write("Before opening logs");
        var window = new LogWindow(log);
        var model = Assert.IsType<LogWindowViewModel>(window.DataContext);
        var list = (ListBox)window.FindName("LogList");
        var uiThread = Environment.CurrentManagedThreadId;
        var updateThreads = new List<int>();
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) =>
            updateThreads.Add(Environment.CurrentManagedThreadId);
        try
        {
            Assert.True(BindingOperations.IsDataBound(list, ItemsControl.ItemsSourceProperty));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => list.Items.Count == 1,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("Before opening logs", ((LogEntry)list.Items[0]).Message);

            await Task.Run(() => log.Write("Newest background entry"));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => list.Items.Count == 2,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("Newest background entry", ((LogEntry)list.Items[0]).Message);

            model.IsPaused = true;
            var paused = list.Items.Cast<LogEntry>().ToArray();
            await Task.Run(() => log.Write("Entry while paused"));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(paused, list.Items.Cast<LogEntry>());
            model.IsPaused = false;
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => list.Items.Count == 3,
                TimeSpan.FromSeconds(2)));

            var selected = list.Items[1];
            list.SelectedItem = selected;
            await Task.Run(() => log.Write("Entry while selecting"));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => list.Items.Count == 4,
                TimeSpan.FromSeconds(2)));
            Assert.Same(selected, list.SelectedItem);

            model.ClearCommand.Execute(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => list.Items.Count == 0,
                TimeSpan.FromSeconds(2)));
            await Task.Run(() => log.Write("Entry after clear"));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => list.Items.Count == 1,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("Entry after clear", ((LogEntry)list.Items[0]).Message);
            Assert.Contains(log.ReadAfter(0), entry => entry.Message == "Before opening logs");
            Assert.All(updateThreads, thread => Assert.Equal(uiThread, thread));
        }
        finally
        {
            window.Close();
        }

        var notificationsAfterClose = 0;
        model.PropertyChanged += (_, _) => notificationsAfterClose++;
        await Task.Run(() => log.Write("After closing logs"));
        Assert.Equal(0, notificationsAfterClose);
        var reopened = new LogWindow(log);
        try
        {
            var reopenedList = (ListBox)reopened.FindName("LogList");
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => reopenedList.Items.Count == 6,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("After closing logs", ((LogEntry)reopenedList.Items[0]).Message);
        }
        finally
        {
            reopened.Close();
        }
    }

    private static TextBlock BoundFeedback(OutputWindowRow row)
    {
        return new()
        {
            DataContext = row,
            Style = (Style)Application.Current.FindResource("OutputFeedbackTextStyle"),
        };
    }

    private static async Task VerifyDirectBindingsAsync(ServiceProvider services)
    {
        var io = services.GetRequiredService<VirtualIoService>();
        var machine = services.GetRequiredService<MachineController>();
        var signals = services.GetRequiredService<IoSignals>();
        // These rows have no owning view refreshing commands. Production styles
        // must follow their nested IO objects without relayed row notifications.
        var light = new OutputWindowRow(signals.Outputs[OutputIo.MachineLight], machine);
        var button = BoundButton(light);
        var notifications = 0;
        light.PropertyChanged += (_, _) => notifications++;
        foreach (var on in new[] { true, false })
        {
            await Task.Run(() => io.SetOutput(OutputIo.MachineLight, on));
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => (string?)button.Content == (on ? "OFF" : "ON"),
                    TimeSpan.FromSeconds(2)));
        }

        Assert.Equal(0, notifications);

        var stopper = new OutputWindowRow(signals.Outputs[OutputIo.NgConveyorStopperUp], machine);
        var feedback = BoundFeedback(stopper);
        await Task.Run(
            () =>
            {
                io.SetOutput(OutputIo.NgConveyorStopperUp, false);
                io.SetInput(InputIo.NgConveyorStopperUp, false);
                io.SetInput(InputIo.NgConveyorStopperDown, true);
            });
        Assert.True(
            await VirtualTest.WaitUntilAsync(() => feedback.Text == "Matched", TimeSpan.FromSeconds(2)));
        await Task.Run(() => io.SetInput(InputIo.NgConveyorStopperDown, false));
        Assert.True(
            await VirtualTest.WaitUntilAsync(
                () => feedback.Text == "Not matched",
                TimeSpan.FromSeconds(2)));
        stopper.ToggleCommand.Execute(null);
        Assert.True(io.GetOutput(stopper.Io.Signal));
        Assert.True(stopper.ToggleCommand.CanExecute(null));
        stopper.ToggleCommand.Execute(null);
        Assert.False(io.GetOutput(stopper.Io.Signal));
        Assert.Equal("Not matched", feedback.Text);
        // A second click reads the device even before the first DO snapshot arrives.
        var signal = signals.Outputs[OutputIo.MainConveyorRun];
        var pending = new OutputWindowRow(
            new IoOutputStatus(signal.Signal, signal.Area, signal.Section, io, signals.Inputs),
            machine);
        var pendingButton = BoundButton(pending);
        pending.ToggleCommand.Execute(null);
        Assert.Null(pending.Io.IsOn);
        Assert.True(io.GetOutput(signal.Signal));
        Assert.True(pendingButton.IsEnabled);
        Assert.Same(pending.ToggleCommand, pendingButton.Command);
        pendingButton.Command.Execute(null);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
    }

}
