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
using IBTM.Inspection;
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
                        Inspection = true,
                        NgCarrierTransfer = false,
                        NgShuttle = false,
                        PickupBoltFeeder = false,
                        ShootingBoltFeeder = false,
                    },
                })
            .AddSingleton<ILightController>(new TestLight())
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
            await VerifyBackgroundDisplayBindingsAsync(services);
            manual.Activate();
            const OutputIo output = OutputIo.MainConveyorRun;
            for (var reopen = 0; reopen < 2; reopen++)
            {
                Assert.True(openButton.IsEnabled);
                window = new OutputWindow(signals, machine, state);
                if (reopen == 0)
                    await VerifyDirectBindingsAsync(services, window);
                var row = window.Rows.Single(candidate => candidate.Io.Signal == output);
                var manualRow = manual.Conveyors.Single(candidate => candidate.Io.Signal == output);
                // These are real WPF command subscribers with the production bindings.
                var outputButton = BindOutputRow(window, row).Button;
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
                var feedback = BindOutputRow(
                    window,
                    window.Rows.Single(candidate => candidate.Io.Signal == OutputIo.NgConveyorStopperUp)).Feedback;
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
                log.Snapshot(),
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
            new Binding("VirtualIo.AutoResponseEnabled") { Source = input });
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
        var conveyorEnabled = new CheckBox();
        conveyorEnabled.SetBinding(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding("Units.MainConveyor") { Source = operation, Mode = BindingMode.OneWay });
        foreach (var enabled in new[] { false, true })
        {
            operation.Units.MainConveyor = enabled;
            operation.Activate();
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => conveyorEnabled.IsChecked == enabled,
                TimeSpan.FromSeconds(2)));
        }

        var sensor = new CheckBox();
        foreach (var (path, signal) in new[]
        {
            ("Conveyor.EntryCarrierDetected", InputIo.MainConveyorEntryCarrierDetected),
            ("PcbPlacementWork.CarrierPresent", InputIo.PcbPlacementCarrierPresent),
            ("NgTransfer.CarrierDetected", InputIo.NgCarrierDetected),
            ("NgShuttle.Feedback.CarrierDetected", InputIo.NgShuttleCarrierDetected),
        })
        {
            sensor.SetBinding(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(path) { Source = operation, Mode = BindingMode.OneWay });
            foreach (var detected in new[] { true, false })
            {
                await Task.Run(() => io.SetInput(signal, detected));
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => sensor.IsChecked == detected,
                    TimeSpan.FromSeconds(2)));
            }
        }

        var running = new CheckBox();
        foreach (var signal in new[] { operation.MainConveyorRun, operation.NgConveyorRun })
        {
            Assert.Same(services.GetRequiredService<IoSignals>().Outputs[signal.Signal], signal);
            running.SetBinding(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(nameof(IoOutputStatus.IsOn)) { Source = signal, Mode = BindingMode.OneWay });
            foreach (var on in new[] { true, false })
            {
                await Task.Run(() => io.SetOutput(signal.Signal, on));
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => running.IsChecked == on,
                    TimeSpan.FromSeconds(2)));
            }
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

            var connect = (Button)adc.FindName("ConnectButton");
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => !bus.IsOpen && connect.IsEnabled,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("Disconnected", adc.ConnectionStatus);
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => bus.IsOpen && connect.IsEnabled,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("Virtual | 19200", adc.ConnectionStatus);
        }
        finally
        {
            adc.Close();
            bus.Close();
        }

        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var main = services.GetRequiredService<MainViewModel>();
        await main.NavigateCommand.ExecuteAsync(AppPage.StationTeaching);
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        var page = new Border();
        page.SetBinding(
            UIElement.IsEnabledProperty,
            new Binding(nameof(MainViewModel.CurrentPageEnabled)) { Source = main });
        var preview = new Image();
        preview.SetBinding(
            Image.SourceProperty,
            new Binding(nameof(StationTeachingViewModel.LiveImage)) { Source = teaching });
        var light = Assert.IsType<TestLight>(services.GetRequiredService<ILightController>());
        using var releaseStop = new ManualResetEventSlim();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => preview.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true },
                TimeSpan.FromSeconds(2)));
            light.BeforeOff = () =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                stopEntered.TrySetResult();
                Assert.True(releaseStop.Wait(TimeSpan.FromSeconds(2)));
            };
            light.FailOff = true;
            var stopping = main.NavigateCommand.ExecuteAsync(AppPage.Settings);
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stopping.IsCompleted);
            Assert.Equal(AppPage.StationTeaching, main.SelectedPage);
            Assert.False(page.IsEnabled);
            Assert.False(main.RecipeEditingEnabled);
            Assert.False(main.NavigateCommand.CanExecute(AppPage.Settings));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => preview.Source is null,
                TimeSpan.FromSeconds(2)));
            releaseStop.Set();
            await stopping;
            Assert.False(teaching.Inspector.IsLiveView);
            Assert.Equal(AppPage.StationTeaching, main.SelectedPage);
            Assert.Contains(light.OffFailure.Message, main.NavigationError);
            Assert.True(await VirtualTest.WaitUntilAsync(() => page.IsEnabled, TimeSpan.FromSeconds(2)));
            var failure = await Assert.ThrowsAsync<IOException>(teaching.ShutdownAsync);
            Assert.Same(light.OffFailure, failure);
            Assert.Contains(nameof(TestLight.TurnOff), failure.StackTrace);
            light.FailOff = false;
            await main.NavigateCommand.ExecuteAsync(AppPage.Settings);
            Assert.Equal(AppPage.Settings, main.SelectedPage);
            Assert.Null(main.NavigationError);

            var settings = services.GetRequiredService<SettingsViewModel>();
            var lightTest = settings.TestLightCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(() => settings.LightTestOn, TimeSpan.FromSeconds(2)));
            releaseStop.Reset();
            stopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var returning = main.NavigateCommand.ExecuteAsync(AppPage.StationTeaching);
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(AppPage.Settings, main.SelectedPage);
            Assert.False(page.IsEnabled);
            Assert.False(lightTest.IsCompleted);
            io.SetInput(InputIo.AutoMode, false);
            releaseStop.Set();
            await returning;
            await lightTest;
            Assert.Equal(AppPage.Operation, main.SelectedPage);
            Assert.False(light.IsOn);
            Assert.False(settings.LightTestOn);
            io.SetInput(InputIo.AutoMode, true);
            await main.NavigateCommand.ExecuteAsync(AppPage.StationTeaching);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            Assert.True(light.IsOn);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);

            var live = new CheckBox();
            live.SetBinding(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding("Inspector.IsLiveView")
                { Source = teaching, Mode = BindingMode.OneWay });
            var state = services.GetRequiredService<MachineState>();
            foreach (var hardwareReset in new[] { false, true })
            {
                await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => live.IsChecked == true && preview.Source is not null,
                    TimeSpan.FromSeconds(2)));
                Assert.True(light.IsOn);
                state.SetError(MachineAlarm.Inspection);
                Assert.True(machine.CanReset);
                if (hardwareReset)
                    await Task.Run(() => io.SetInput(InputIo.ResetButton, true));
                else
                    await main.ResetCommand.ExecuteAsync(null);

                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => live.IsChecked == false && preview.Source is null
                        && !state.IsRunning && state.Alarm == MachineAlarm.None,
                    TimeSpan.FromSeconds(2)));
                Assert.False(light.IsOn);
                Assert.Null(teaching.CameraError);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                io.SetInput(InputIo.ResetButton, false);
            }

            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            Assert.True(light.IsOn);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);

            var reference = services.GetRequiredService<CarrierReferenceSettings>();
            reference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
            reference.LowerRightLocatingPin = new() { X = 1, Y = 0 };
            await services.GetRequiredService<InspectionGantry>().HomeHorizontalAsync();
            teaching.RecipeEditor.Name = "ThreadingScan";
            teaching.ScanOverlap = 0;
            var liveButton = new Button { Command = teaching.ToggleLiveViewCommand };
            var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseStop.Reset();
            light.BeforeOn = () =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                scanStarted.TrySetResult();
                Assert.True(releaseStop.Wait(TimeSpan.FromSeconds(2)));
            };
            light.BeforeOff = () => Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
            var scan = teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(liveButton.IsEnabled);
            var onCalls = light.OnCalls;
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            Assert.False(teaching.Inspector.IsLiveView);
            Assert.Equal(onCalls, light.OnCalls);
            teaching.CaptureCarrierImagesCommand.Cancel();
            releaseStop.Set();
            await scan.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(teaching.CameraError);
            Assert.True(liveButton.IsEnabled);

            // Selection changes after the image commit must not undo the successful save.
            light.BeforeOn = null;
            var next = teaching.FilteredPoints.Single(
                point => point.Position.Target == TeachingTarget.CarrierLowerRightLocatingPin);
            teaching.RecipeEditor.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RecipeEditor.ActiveName))
                    teaching.SelectedPoint = next;
            };
            Assert.True(teaching.CaptureCarrierImagesCommand.CanExecute(null));
            await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
            Assert.Same(next, teaching.SelectedPoint);
            Assert.True(teaching.HasCarrierImages);
            var saved = await services.GetRequiredService<RecipeStore>()
                .LoadRecipeAsync(teaching.RecipeEditor.ActiveName);
            Assert.Equal(teaching.CarrierImages.Count, saved.CarrierImages.Count);
            Assert.Equal(
                saved.Name,
                services.GetRequiredService<MachineStore>()
                    .LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
            Assert.Null(teaching.CameraError);
        }
        finally
        {
            releaseStop.Set();
            light.BeforeOn = null;
            light.BeforeOff = null;
            light.FailOff = false;
            await teaching.ShutdownAsync();
        }
    }

    private sealed class TestLight : ILightController
    {
        public Action? BeforeOn { get; set; }
        public Action? BeforeOff { get; set; }
        public int OnCalls { get; private set; }
        public bool IsOn { get; private set; }
        public bool FailOff { get; set; }
        public IOException OffFailure { get; } = new("Simulated live light OFF failure.");

        public void Initialize() { }
        public void SetLevel(int channel, int level) { }
        public void TurnOn(int channel)
        {
            OnCalls++;
            IsOn = true;
            BeforeOn?.Invoke();
        }
        public void TurnOffAll()
        {
            IsOn = false;
        }

        public void TurnOff(int channel)
        {
            BeforeOff?.Invoke();
            if (FailOff)
                throw OffFailure;
            IsOn = false;
        }
    }


    private static (Button Button, TextBlock Feedback) BindOutputRow(OutputWindow window, OutputWindowRow row)
    {
        var list = ((Grid)window.Content).Children.OfType<ListBox>().Single();
        var presenter = new ContentPresenter { Content = row, ContentTemplate = list.ItemTemplate };
        presenter.ApplyTemplate();
        return (
            (Button)list.ItemTemplate.FindName("ToggleOutput", presenter),
            (TextBlock)list.ItemTemplate.FindName("FeedbackStatus", presenter));
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
            Assert.Contains(log.Snapshot(), entry => entry.Message == "Before opening logs");
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

    private static async Task VerifyDirectBindingsAsync(ServiceProvider services, OutputWindow window)
    {
        var io = services.GetRequiredService<VirtualIoService>();
        var machine = services.GetRequiredService<MachineController>();
        var signals = services.GetRequiredService<IoSignals>();
        // These rows have no owning view refreshing commands. The production template
        // must follow their nested IO objects without relayed row notifications.
        var light = new OutputWindowRow(signals.Outputs[OutputIo.MachineLight], machine);
        var button = BindOutputRow(window, light).Button;
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
        var feedback = BindOutputRow(window, stopper).Feedback;
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
        var pendingButton = BindOutputRow(window, pending).Button;
        pending.ToggleCommand.Execute(null);
        Assert.Null(pending.Io.IsOn);
        Assert.True(io.GetOutput(signal.Signal));
        Assert.True(pendingButton.IsEnabled);
        Assert.Same(pending.ToggleCommand, pendingButton.Command);
        pendingButton.Command.Execute(null);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
    }

}
