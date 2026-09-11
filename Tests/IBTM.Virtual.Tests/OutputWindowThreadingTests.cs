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
                                var lifecycle = new MachineLifecycleTests();
                                await lifecycle.VerifyTeachingSelectionIgnoresLateCameraCompletionAsync(false);
                                await lifecycle.VerifyTeachingSelectionIgnoresLateCameraCompletionAsync(true);
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

    private static async Task VerifyIndependentTeachingAsync(IServiceProvider services)
    {
        var units = services.GetRequiredService<UnitSettings>();
        var state = services.GetRequiredService<MachineState>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var unrelated = (VirtualMotionService)services.GetRequiredKeyedService<IAxisMotion>(
            MotionGroup.PcbSupply);
        var inspection = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.InspectionGantry);
        await inspection.HomeHorizontalAsync(1000);
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        try
        {
            units.PcbSupply = true;
            unrelated.SetAlarm(MotionAxis.X, true);
            state.SetError(MachineAlarm.MotionUnavailable, new IOException("Supply axis alarm."));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => teaching.StepCommand.CanExecute(TeachingDirection.XPlus),
                TimeSpan.FromSeconds(2)));
            var before = inspection.GetPosition();
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + teaching.StepDistance, inspection.GetPosition().X, 3);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);

            var monitor = services.GetRequiredService<MotionWindowViewModel>();
            var axis = monitor.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => monitor.HomeAxisCommand.CanExecute(axis), TimeSpan.FromSeconds(2)));
            await monitor.HomeAxisCommand.ExecuteAsync(axis);
            Assert.True(inspection.GetAxisState(MotionAxis.X).Homed);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);

            inspection.SetServo(MotionAxis.X, false);
            teaching.SelectedPoint = teaching.FilteredPoints.Single(
                point => point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
            var taughtPoint = teaching.SelectedPoint;
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => !teaching.StepCommand.CanExecute(TeachingDirection.XPlus),
                TimeSpan.FromSeconds(2)));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => teaching.TeachCurrentPositionCommand.CanExecute(null),
                TimeSpan.FromSeconds(2)));
            Assert.False(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(inspection.GetPosition().X, taughtPoint.X, 3);
            Assert.True(teaching.ToggleLiveViewCommand.CanExecute(null));
        }
        finally
        {
            units.PcbSupply = false;
            unrelated.SetAlarm(MotionAxis.X, false);
            inspection.SetServo(MotionAxis.X, true);
            state.ClearError();
        }
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
            var resetButton = new Button { Command = main.ResetCommand };
            io.SetInput(InputIo.EmergencyStop1Pressed, true);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetOutput(OutputIo.Buzzer), TimeSpan.FromSeconds(2)));
            Assert.False(machine.CanReset);
            Assert.True(resetButton.IsEnabled);
            await main.ResetCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => !io.GetOutput(OutputIo.Buzzer), TimeSpan.FromSeconds(2)));
            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            Assert.True(io.GetOutput(OutputIo.TowerLampRed));
            io.SetInput(InputIo.EmergencyStop1Pressed, false);
            // The virtual safety relay, like the equipment, restores contactor power on hardware RESET.
            io.SetInput(InputIo.ResetButton, true);
            Assert.True(resetButton.IsEnabled);
            await main.ResetCommand.ExecuteAsync(null);
            io.SetInput(InputIo.ResetButton, false);
            using (services.GetRequiredService<OperationCancellation>().Link())
            {
                Assert.False(machine.CanReset);
                Assert.True(resetButton.IsEnabled);
                await main.ResetCommand.ExecuteAsync(null);
            }

            await VerifyIndependentTeachingAsync(services);
            io.AutoResponseEnabled = false;
            await VerifyBackgroundDisplayBindingsAsync(services);
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
                    window.Rows.Single(candidate => candidate.Io.Signal == OutputIo.PcbPlacementStopperDown)).Feedback;
                await Task.Run(
                    () =>
                    {
                        io.SetInput(InputIo.PcbPlacementStopperUp, true);
                        io.SetInput(InputIo.PcbPlacementStopperDown, true);
                    });
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => feedback.Text == "Input conflict",
                        TimeSpan.FromSeconds(2)));
                await Task.Run(() => io.SetInput(InputIo.PcbPlacementStopperUp, false));
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
                Assert.True(io.GetOutput(output)); // Closing the view is not a STOP command.
                window = null;
                manualStop.Command.Execute(null);
                Assert.False(io.GetOutput(output));
            }

            await main.NavigateCommand.ExecuteAsync(AppPage.ManualHardware);
            Assert.Equal(AppPage.ManualHardware, main.SelectedPage);
            var conveyor = manual.Conveyors.Single(row => row.Io.Signal == output);
            var manualRun = conveyor.RunCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(output));
            await main.NavigateCommand.ExecuteAsync(AppPage.Operation);
            Assert.Equal(AppPage.Operation, main.SelectedPage);
            Assert.True(io.GetOutput(output));
            Assert.False(manualRun.IsCompleted);
            await main.NavigateCommand.ExecuteAsync(AppPage.ManualHardware);
            Assert.Equal(AppPage.ManualHardware, main.SelectedPage);
            Assert.True(io.GetOutput(output));
            conveyor.StopCommand.Execute(null);
            await manualRun.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(output));

            Assert.All(notifications, id => Assert.Equal(uiThread, id));
            Assert.DoesNotContain(
                log.Snapshot(),
                entry => entry.Message.Contains("Display worker stopped"));
        }
        finally
        {
            if (window is not null)
            {
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
        var frames = (TextBox)adc.FindName("FrameLogBox");
        var uiThread = Environment.CurrentManagedThreadId;
        var updates = new List<int>();
        adc.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AdcProtocolWindow.FrameLogText))
                updates.Add(Environment.CurrentManagedThreadId);
        };
        try
        {
            Assert.True(BindingOperations.IsDataBound(frames, TextBox.TextProperty));
            Assert.True(frames.IsReadOnly);
            await Task.Run(() => bus.ReadDeviceInformationAsync(0));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => frames.Text.Contains("RX RAW") && frames.Text.Contains("TX"),
                TimeSpan.FromSeconds(2)));
            Assert.True(frames.Text.IndexOf("RX RAW") < frames.Text.IndexOf("TX"));
            adc.IsLogPaused = true;
            frames.SelectAll();
            var selectedFrames = frames.SelectedText;
            Assert.Contains(Environment.NewLine, selectedFrames);
            await Task.Run(() => bus.ReadDeviceInformationAsync(1));
            await adc.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(selectedFrames, frames.SelectedText);
            adc.IsLogPaused = false;
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => frames.Text.Length > selectedFrames.Length,
                TimeSpan.FromSeconds(2)));
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
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
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
            // Device failures are reported at the teaching command boundary.
            light.BeforeOn = () => throw new InvalidOperationException("Scan light ON failed.");
            await teaching.CaptureCarrierImageCommand.ExecuteAsync(null);
            Assert.Equal("Scan light ON failed.", teaching.CameraError);
            Assert.False(state.IsRunning);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);

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
            var scan = teaching.CaptureCarrierImageCommand.ExecuteAsync(null);
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(liveButton.IsEnabled);
            var onCalls = light.OnCalls;
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            Assert.False(teaching.Inspector.IsLiveView);
            Assert.Equal(onCalls, light.OnCalls);
            teaching.CaptureCarrierImageCommand.Cancel();
            releaseStop.Set();
            await scan.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(teaching.CameraError);
            Assert.True(liveButton.IsEnabled);

            // Selection changes after the image commit must not undo the successful save.
            light.BeforeOn = null;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(
                point => point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
            var next = teaching.FilteredPoints.Single(
                point => point.Position.Target == TeachingTarget.CarrierLowerRightLocatingPin);
            teaching.RecipeEditor.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RecipeEditor.ActiveName))
                    teaching.SelectedPoint = next;
            };
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => teaching.CaptureCarrierImageCommand.CanExecute(null), TimeSpan.FromSeconds(2)));
            await teaching.CaptureCarrierImageCommand.ExecuteAsync(null);
            Assert.Same(next, teaching.SelectedPoint);
            Assert.True(teaching.HasCarrierImages);
            Assert.Single(teaching.CarrierImages);
            var gantry = services.GetRequiredService<InspectionGantry>();
            await gantry.MoveAxisAsync(MotionAxis.X, 10, 10_000);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            Assert.True(teaching.Inspector.IsLiveView);
            var liveLightOnCalls = light.OnCalls;
            await teaching.CaptureCarrierImageCommand.ExecuteAsync(null);
            Assert.True(teaching.Inspector.IsLiveView);
            Assert.Equal(liveLightOnCalls, light.OnCalls);
            Assert.Equal(0, teaching.SelectedCameraTab);
            Assert.True(light.IsOn);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            Assert.Equal(2, teaching.CarrierImages.Count);
            Assert.Equal(0, teaching.CarrierImages[0].Center.X);
            Assert.Equal(10, teaching.CarrierImages[1].Center.X);
            Assert.Equal(10, gantry.Feedback.GetPosition().X);
            var saved = await services.GetRequiredService<RecipeStore>()
                .LoadRecipeAsync(teaching.RecipeEditor.ActiveName);
            Assert.Equal(teaching.CarrierImages.Count, saved.CarrierImages.Count);
            Assert.Equal(
                saved.Name,
                services.GetRequiredService<MachineStore>()
                    .LoadSettings().Get<RecipeSelectionSettings>().LastRecipeName);
            Assert.Null(teaching.CameraError);
            teaching.AddBoltPointCommand.Execute(null);
            var roiBolt = teaching.SelectedPoint!;
            var fov = teaching.SelectedFov!;
            teaching.MillimetersPerPixel = 0.02;
            Assert.True(teaching.TeachFovRegionCommand.CanExecute(Rect.Empty));
            await teaching.TeachFovRegionCommand.ExecuteAsync(new Rect(200, 30, 60, 80));
            Assert.Equal(new PixelRegion(200, 30, 60, 80), teaching.SelectedFov!.Region);
            Assert.Equal(fov.Center, teaching.SelectedFov.Center);
            Assert.Equal(fov.Center.X + (230 - fov.Image.PixelWidth / 2.0) * 0.02, roiBolt.X, 6);
            Assert.Equal(fov.Center.Y + (70 - fov.Image.PixelHeight / 2.0) * 0.02, roiBolt.Y, 6);
            teaching.SelectedPoint = roiBolt;
            await teaching.TeachFovRegionCommand.ExecuteAsync(new Rect(210, 40, 50, 60));
            teaching.MillimetersPerPixel = 0.04;
            teaching.SelectedPoint = roiBolt;
            Assert.True(teaching.TeachFovRegionCommand.CanExecute(teaching.FovRegion!.Value));
            await teaching.TeachFovRegionCommand.ExecuteAsync(teaching.FovRegion!.Value);
            saved = await services.GetRequiredService<RecipeStore>()
                .LoadRecipeAsync(teaching.RecipeEditor.ActiveName);
            var roiFov = Assert.Single(saved.CarrierImages, image => image.Region is not null);
            Assert.Equal(new PixelRegion(210, 40, 50, 60), roiFov.Region);
            Assert.Equal(roiBolt.BoltNumber, roiFov.BoltNumber);
            Assert.Equal(fov.Center.X, roiFov.Center.X);
            Assert.Equal(fov.Center.Y, roiFov.Center.Y);
            Assert.Equal(0.04, saved.CarrierImageMillimetersPerPixel);
            var savedBolt = saved.Pcb.BoltPoints.Single(bolt => bolt.Number == roiBolt.BoltNumber);
            Assert.Equal(
                fov.Center.X + (235 - fov.Image.PixelWidth / 2.0) * 0.04 - reference.UpperLeftLocatingPin.X,
                savedBolt.X!.Value, 6);
            Assert.Equal(
                fov.Center.Y + (70 - fov.Image.PixelHeight / 2.0) * 0.04 - reference.UpperLeftLocatingPin.Y,
                savedBolt.Y!.Value, 6);
            Assert.Equal(10, gantry.Feedback.GetPosition().X);
            Assert.True(teaching.Inspector.HasPosition(roiBolt.Position.Bolt!));
            teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
            teaching.AddBoltPointCommand.Execute(null);
            var secondBolt = teaching.SelectedPoint!;
            Assert.Equal(roiBolt.BoltNumber, secondBolt.BoltNumber);
            teaching.SelectedFov = teaching.CarrierImages[0];
            await teaching.TeachFovRegionCommand.ExecuteAsync(new Rect(40, 60, 80, 80));
            Assert.True(teaching.Inspector.HasPosition(roiBolt.Position.Bolt!));
            Assert.True(teaching.Inspector.HasPosition(secondBolt.Position.Bolt!));
            teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
            teaching.SelectedPoint = roiBolt;
            teaching.RemoveBoltPointCommand.Execute(null);
            Assert.False(teaching.Inspector.HasPosition(roiBolt.Position.Bolt!));
            Assert.True(teaching.Inspector.HasPosition(secondBolt.Position.Bolt!));
            teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
            teaching.SelectedPoint = secondBolt;
            teaching.RemoveBoltPointCommand.Execute(null);
            Assert.All(teaching.CarrierImages, image => Assert.Null(image.Region));
            teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
            teaching.SelectedFov = teaching.CarrierImages[1];

            // Barcode teaching uses the frozen FOV while live remains visible above it.
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            teaching.SelectedPoint = teaching.FilteredPoints.Single(
                point => point.Position.Target == TeachingTarget.DataMatrix);
            Assert.Equal(0, teaching.SelectedCameraTab);
            Assert.False(teaching.TeachCurrentPositionCommand.CanExecute(null));
            Assert.True(teaching.TeachFovRegionCommand.CanExecute(Rect.Empty));
            await teaching.TeachFovRegionCommand.ExecuteAsync(new Rect(20, 30, 60, 80));
            Assert.True(teaching.Inspector.IsLiveView);
            Assert.Equal(fov.Center, teaching.Inspector.GetBarcodeFov(HeatSinkSlot.HeatSink1).Center);
            Assert.Equal(new PixelRegion(20, 30, 60, 80), teaching.SelectedFov!.Region);
            saved = await services.GetRequiredService<RecipeStore>()
                .LoadRecipeAsync(teaching.RecipeEditor.ActiveName);
            var barcode = Assert.Single(saved.CarrierImages, image => image.IsBarcode);
            Assert.Null(barcode.BoltNumber);
            Assert.Equal(HeatSinkSlot.HeatSink1, barcode.HeatSink);
            Assert.Equal(fov.Center.X, barcode.Center.X);

            // Reassigning a barcode clears only its previous FOV; the other heat sink stays independent.
            teaching.SelectedPoint = teaching.FilteredPoints.Single(
                point => point.Position.Target == TeachingTarget.DataMatrix);
            teaching.SelectedFov = teaching.CarrierImages[0];
            await teaching.TeachFovRegionCommand.ExecuteAsync(new Rect(30, 40, 70, 80));
            Assert.Single(teaching.CarrierImages, image => image.IsBarcode);
            teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(
                point => point.Position.Target == TeachingTarget.DataMatrix);
            teaching.SelectedFov = teaching.CarrierImages[1];
            await teaching.TeachFovRegionCommand.ExecuteAsync(new Rect(100, 80, 80, 80));
            Assert.True(teaching.Inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
            Assert.True(teaching.Inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink2));
            Assert.Equal(0, teaching.SelectedCameraTab);
            Assert.True(teaching.Inspector.IsLiveView);
            Assert.Equal(10, gantry.Feedback.GetPosition().X);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            foreach (var closeTeaching in new[] { false, true })
            {
                var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseStop.Reset();
                light.BeforeOn = () =>
                {
                    captureStarted.TrySetResult();
                    Assert.True(releaseStop.Wait(TimeSpan.FromSeconds(2)));
                };
                var capture = teaching.CaptureCarrierImageCommand.ExecuteAsync(null);
                await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var closing = closeTeaching ? teaching.ShutdownAsync() : Task.CompletedTask;
                if (!closeTeaching)
                    io.SetInput(InputIo.AutoMode, false);
                releaseStop.Set();
                await Task.WhenAll(capture, closing).WaitAsync(TimeSpan.FromSeconds(2));
                saved = await services.GetRequiredService<RecipeStore>()
                    .LoadRecipeAsync(teaching.RecipeEditor.ActiveName);
                Assert.Equal(2, saved.CarrierImages.Count);
                Assert.Null(teaching.CameraError);
                Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
                light.BeforeOn = null;
                io.SetInput(InputIo.AutoMode, true);
                teaching.Activate();
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => teaching.CarrierImages.Count == 2 && teaching.CaptureCarrierImageCommand.CanExecute(null),
                    TimeSpan.FromSeconds(2)));
            }
            await teaching.ClearCarrierImagesCommand.ExecuteAsync(null);
            Assert.Empty(teaching.CarrierImages);
            saved = await services.GetRequiredService<RecipeStore>()
                .LoadRecipeAsync(teaching.RecipeEditor.ActiveName);
            Assert.Empty(saved.CarrierImages);
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
        var text = (TextBox)window.FindName("LogText");
        var uiThread = Environment.CurrentManagedThreadId;
        var updateThreads = new List<int>();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LogWindowViewModel.Text))
                updateThreads.Add(Environment.CurrentManagedThreadId);
        };
        try
        {
            Assert.True(BindingOperations.IsDataBound(text, TextBox.TextProperty));
            Assert.True(text.IsReadOnly);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => text.Text.Contains("Before opening logs"),
                TimeSpan.FromSeconds(2)));

            await Task.Run(() => log.Write("Newest background entry"));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => text.Text.Contains("Newest background entry"),
                TimeSpan.FromSeconds(2)));
            Assert.StartsWith(log.Snapshot().Last().Text, text.Text);

            model.IsPaused = true;
            var paused = text.Text;
            text.SelectAll();
            Assert.Equal(paused, text.SelectedText);
            Assert.Contains(Environment.NewLine, text.SelectedText);
            await Task.Run(() => log.Write("Entry while paused"));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(paused, text.Text);
            Assert.Equal(paused, text.SelectedText);
            model.IsPaused = false;
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => text.Text.Contains("Entry while paused"),
                TimeSpan.FromSeconds(2)));

            model.ClearCommand.Execute(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => text.Text.Length == 0,
                TimeSpan.FromSeconds(2)));
            await Task.Run(() => log.Write("Entry after clear"));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => text.Text.Contains("Entry after clear"),
                TimeSpan.FromSeconds(2)));
            Assert.Equal(log.Snapshot().Last().Text, text.Text);
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
            var reopenedText = (TextBox)reopened.FindName("LogText");
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => reopenedText.Text.Contains("After closing logs"),
                TimeSpan.FromSeconds(2)));
            Assert.StartsWith(log.Snapshot().Last().Text, reopenedText.Text);
            Assert.Contains("Before opening logs", reopenedText.Text);
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

        var stopper = new OutputWindowRow(signals.Outputs[OutputIo.PcbPlacementStopperDown], machine);
        var feedback = BindOutputRow(window, stopper).Feedback;
        await Task.Run(
            () =>
            {
                io.SetOutput(OutputIo.PcbPlacementStopperDown, false);
                io.SetInput(InputIo.PcbPlacementStopperUp, true);
                io.SetInput(InputIo.PcbPlacementStopperDown, false);
            });
        Assert.True(
            await VirtualTest.WaitUntilAsync(() => feedback.Text == "Matched", TimeSpan.FromSeconds(2)));
        await Task.Run(() => io.SetInput(InputIo.PcbPlacementStopperUp, false));
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
