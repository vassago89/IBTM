using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.Extensions.Logging;

namespace IBTM.Virtual.Tests;
// A plain WPF Application supplies resources/Dispatcher only. Never start IBTM.App.
[CollectionDefinition("Output window UI", DisableParallelization = true)]
public sealed class OutputWindowUiCollection;

[Collection("Output window UI")]
public sealed class OutputWindowThreadingTests
{
    [Fact]
    public async Task BoundConveyorButtonsUpdateAcrossOffCloseAndReopen()
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
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var unrelated = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.PcbSupply);
        var inspection = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.InspectionGantry);
        await inspection.HomeHorizontalAsync(1000);
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        try
        {
            units.PcbSupply = true;
            unrelated.Initialize();
            await unrelated.ResetAsync();
            await unrelated.HomeAsync(MotionAxis.Z, 1000);
            await unrelated.HomeHorizontalAsync(1000);
            var supplySettings = services.GetRequiredService<IBTM.PcbSupply.PcbSupplySettings>();
            supplySettings.RotationZ = supplySettings.HandoffPosition.Z = 10;
            var io = services.GetRequiredService<VirtualIoService>();
            io.SetInput(InputIo.PcbSupplyRotated, false);
            io.SetInput(InputIo.PcbSupplyUnrotated, true);
            // Manual XY buttons remain usable away from the taught travel height.
            var z = typeof(VirtualMotionService).GetField("_z",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            z.SetValue(unrelated, 9d);
            teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
            teaching.Activate();
            var jog = new Button { Command = teaching.JogCommand, CommandParameter = TeachingDirection.XPlus };
            var step = new Button { Command = teaching.StepCommand, CommandParameter = TeachingDirection.XPlus };
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => teaching.Motion.Position.Z == 9 && jog.IsEnabled && step.IsEnabled,
                TimeSpan.FromSeconds(2)));
            z.SetValue(unrelated, 10d);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => jog.IsEnabled && step.IsEnabled, TimeSpan.FromSeconds(2)),
                $"Position={teaching.Motion.Position}; hint={teaching.MotionHint}; busy={state.IsRunning}; "
                + $"predicate={teaching.JogCommand.CanExecute(TeachingDirection.XPlus)}");

            teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
            z.SetValue(unrelated, 9d);
            teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => teaching.Motion.Position.Z == 9 && jog.IsEnabled && step.IsEnabled,
                TimeSpan.FromSeconds(2)));
            teaching.Deactivate();
            z.SetValue(unrelated, 10d);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => teaching.Motion.Position.Z == 10, TimeSpan.FromSeconds(2)));
            teaching.Activate();
            Assert.True(jog.IsEnabled);
            Assert.True(step.IsEnabled);

            teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
            unrelated.SetAlarm(MotionAxis.X, true);
            unrelated.SetServo(MotionAxis.X, false);
            state.SetError(MachineAlarm.MotionUnavailable, new IOException("Supply axis alarm."));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => teaching.StepCommand.CanExecute(TeachingDirection.XPlus),
                TimeSpan.FromSeconds(2)));
            var before = inspection.Position;
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + teaching.StepDistance, inspection.Position.X, 3);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);

            var monitor = services.GetRequiredService<MotionWindowViewModel>();
            var axis = monitor.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            var motionWindow = new MotionWindow(monitor);
            try
            {
                var axisList = ((Grid)motionWindow.Content).Children.OfType<ListBox>().Single();
                var axisView = (Grid)axisList.ItemTemplate.LoadContent();
                axisView.DataContext = axis;
                var buttons = axisView.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Same(axis.ToggleServoCommand, buttons[0].Command);
                Assert.Same(axis.HomeCommand, buttons[1].Command);
                Assert.All(buttons, button => Assert.Null(button.CommandParameter));
            }
            finally
            {
                motionWindow.Close();
            }
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => axis.HomeCommand.CanExecute(null), TimeSpan.FromSeconds(2)));
            await axis.HomeCommand.ExecuteAsync(null);
            Assert.True(inspection.GetAxisState(MotionAxis.X).Homed);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);

            inspection.SetServo(MotionAxis.X, false);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => !jog.IsEnabled && !step.IsEnabled, TimeSpan.FromSeconds(2)));
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
            Assert.Equal(inspection.Position.X, taughtPoint.Coordinates!.X, 3);
            Assert.True(teaching.ToggleLiveViewCommand.CanExecute(null));
        }
        finally
        {
            teaching.Deactivate();
            units.PcbSupply = false;
            unrelated.SetAlarm(MotionAxis.X, false);
            unrelated.SetServo(MotionAxis.X, true);
            inspection.SetServo(MotionAxis.X, true);
            state.ClearError();
        }
    }

    private static async Task VerifyWindowsAsync(Application app)
    {
        foreach (var resource in new[] { "AppStyles", "MachineStyles", "WorkpieceStyles", "IoWindowStyles" })
            app.Resources.MergedDictionaries.Add(
                new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/IBTM;component/UI/{resource}.xaml"),
                });
        await VerifyLogBindingsAsync();
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(
                new MachineSettings
                {
                    Units = new()
                    {
                        MainConveyor = true,
                        NgConveyor = true,
                        PcbSupply = false,
                        PcbPlacement = false,
                        BoltFastening = false,
                        Inspection = true,
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
            Assert.False(machine.IsResetAllowed);
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
                Assert.False(machine.IsResetAllowed);
                Assert.True(resetButton.IsEnabled);
                await main.ResetCommand.ExecuteAsync(null);
            }

            await VerifyIndependentTeachingAsync(services);
            io.AutoResponseEnabled = false;
            var editor = main.RecipeEditor;
            editor.Name = "MVVM recipe selection";
            await editor.SaveCommand.ExecuteAsync(null);
            Assert.Null(editor.Error);
            var recipeSelector = new ComboBox { ItemsSource = editor.Recipes };
            recipeSelector.SetBinding(
                System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                new Binding(nameof(MainViewModel.SelectedRecipeFile)) { Source = main, Mode = BindingMode.TwoWay });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                editor.NewCommand.Execute(null);
                Assert.True(main.RecipeEditingEnabled);
                recipeSelector.SetCurrentValue(
                    System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                    "MVVM recipe selection");
                await (editor.LoadCommand.ExecutionTask ?? Task.CompletedTask);
                Assert.Null(editor.Error);
                Assert.Equal("MVVM recipe selection", editor.ActiveName);
                Assert.Null(recipeSelector.SelectedItem);
            }

            await VerifyBackgroundDisplayBindingsAsync(services);
            const OutputIo output = OutputIo.MainConveyorRun;
            for (var reopen = 0; reopen < 2; reopen++)
            {
                Assert.True(openButton.IsEnabled);
                window = new OutputWindow(new OutputWindowViewModel(signals, machine));
                if (reopen == 0)
                    await VerifyDirectBindingsAsync(services, window);
                var row = ((OutputWindowViewModel)window.DataContext).Rows.Single(candidate => candidate.Io.Signal == output);
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
                // invoke a command subscriber on the acquisition thread.
                await Task.Run(
                    () => io.SetOutput(OutputIo.MachineLight, !io.GetOutput(OutputIo.MachineLight)));
                var feedback = BindOutputRow(
                    window,
                    ((OutputWindowViewModel)window.DataContext).Rows.Single(candidate => candidate.Io.Signal == OutputIo.PcbPlacementStopperUp)).Feedback;
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
                state.Refresh();
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => state.Available,
                        TimeSpan.FromSeconds(2)));
                Assert.True(state.Available);
                Assert.True(openButton.IsEnabled);
                Assert.False(closed.Task.IsCompleted); // OFF must not close OUTPUTS.
                row.ToggleCommand.Execute(null);
                Assert.True(io.GetOutput(output));
                window.Close();
                Assert.True(closed.Task.IsCompleted);
                Assert.True(io.GetOutput(output)); // Closing the view is not a STOP command.
                window = null;
                await ((IAsyncRelayCommand)manualStop.Command).ExecuteAsync(null);
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

            Assert.True(await main.TryCloseAsync(), main.CloseError);
        }
    }

    private static async Task VerifyBackgroundDisplayBindingsAsync(ServiceProvider services)
    {
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var input = new InputWindow(new InputWindowViewModel(io, services.GetRequiredService<IoSignals>()));
        var response = new CheckBox();
        response.SetBinding(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding("VirtualIo.AutoResponseEnabled") { Source = input.DataContext });
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
        var operationView = new OperationView { DataContext = operation };
        operationView.Measure(new Size(1600, 900));
        operationView.Arrange(new Rect(0, 0, 1600, 900));
        operationView.UpdateLayout();
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
            ("Signals.Inputs[MainConveyorEntryCarrierDetected].IsOn", InputIo.MainConveyorEntryCarrierDetected),
            ("Signals.Inputs[PcbSupplyIpmFixerForward].IsOn", InputIo.PcbSupplyIpmFixerForward),
            ("Signals.Inputs[PcbPlacementVacuumDetected].IsOn", InputIo.PcbPlacementVacuumDetected),
            ("Signals.Inputs[PickupHeadVacuumDetected].IsOn", InputIo.PickupHeadVacuumDetected),
            ("Placement.Station.CarrierPresent", InputIo.PcbPlacementHeatSink1Present),
            ("Signals.Inputs[NgCarrierDetected].IsOn", InputIo.NgCarrierDetected),
            ("Signals.Inputs[NgShuttleCarrierDetected].IsOn", InputIo.NgShuttleCarrierDetected),
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
        foreach (var signal in new[] { OutputIo.MainConveyorRun, OutputIo.NgConveyorRun })
        {
            running.SetBinding(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding($"Signals.Outputs[{signal}].IsOn") { Source = operation, Mode = BindingMode.OneWay });
            foreach (var on in new[] { true, false })
            {
                await Task.Run(() => io.SetOutput(signal, on));
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => running.IsChecked == on,
                    TimeSpan.FromSeconds(2)));
            }
        }

        var bus = new VirtualAdcBus();
        bus.Open("Virtual", 19200);
        var adcModel = new AdcProtocolViewModel(bus, new VirtualAdcBus(), services.GetRequiredService<IIoService>(), new HantasSettings(), machine, services.GetRequiredService<MachineState>());
        var adc = new AdcProtocolWindow(adcModel);
        var frames = (TextBox)adc.FindName("FrameLogBox");
        var uiThread = Environment.CurrentManagedThreadId;
        var updates = new List<int>();
        adcModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AdcProtocolViewModel.FrameLogText))
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
            adcModel.IsLogPaused = true;
            frames.SelectAll();
            var selectedFrames = frames.SelectedText;
            Assert.Contains(Environment.NewLine, selectedFrames);
            await Task.Run(() => bus.ReadDeviceInformationAsync(1));
            await adc.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(selectedFrames, frames.SelectedText);
            adcModel.IsLogPaused = false;
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => frames.Text.Length > selectedFrames.Length,
                TimeSpan.FromSeconds(2)));
            Assert.All(updates, thread => Assert.Equal(uiThread, thread));

            var connect = (Button)adc.FindName("ConnectButton");
            connect.Command.Execute(connect.CommandParameter);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => !bus.IsOpen && connect.IsEnabled,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("Disconnected", adcModel.ConnectionStatus);
            connect.Command.Execute(connect.CommandParameter);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => bus.IsOpen && connect.IsEnabled,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("Virtual | 19200", adcModel.ConnectionStatus);
        }
        finally
        {
            adc.Close();
            bus.Close();
        }


        var teaching = services.GetRequiredService<TeachingViewModel>();
        var main = services.GetRequiredService<MainViewModel>();
        await main.NavigateCommand.ExecuteAsync(AppPage.Teaching);
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        var teachingOutput = teaching.TeachingIoGroups.SelectMany(group => group.Outputs)
            .First(row => row.Output is not null);
        var outputTemplate = (DataTemplate)Application.Current.FindResource("TeachingOutputTemplate");
        var outputPresenter = new ContentPresenter { Content = teachingOutput, ContentTemplate = outputTemplate };
        outputPresenter.ApplyTemplate();
        var outputButton = (Button)outputTemplate.FindName("ToggleOutput", outputPresenter);
        Assert.True(BindingOperations.IsDataBound(outputButton, Button.CommandProperty));
        Assert.Same(teachingOutput.ToggleOutputCommand, outputButton.Command);
        Assert.Null(outputButton.CommandParameter);
        var page = new Border();
        page.SetBinding(
            UIElement.IsEnabledProperty,
            new Binding(nameof(MainViewModel.CurrentPageEnabled)) { Source = main });
        page.Child = new TeachingView { DataContext = teaching };
        page.Measure(new Size(1600, 900));
        page.Arrange(new Rect(0, 0, 1600, 900));
        page.UpdateLayout();
        var preview = new Image();
        preview.SetBinding(
            Image.SourceProperty,
            new Binding(nameof(TeachingViewModel.LiveImage)) { Source = teaching });
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
            Assert.Equal(AppPage.Teaching, main.SelectedPage);
            Assert.False(page.IsEnabled);
            Assert.False(main.RecipeEditingEnabled);
            Assert.False(main.NavigateCommand.CanExecute(AppPage.Settings));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => preview.Source is null,
                TimeSpan.FromSeconds(2)));
            releaseStop.Set();
            await stopping;
            Assert.False(teaching.Inspection.IsLiveView);
            Assert.Equal(AppPage.Teaching, main.SelectedPage);
            Assert.Contains(light.OffFailure.Message, main.NavigationError);
            Assert.True(await VirtualTest.WaitUntilAsync(() => page.IsEnabled, TimeSpan.FromSeconds(2)));
            var failure = await Assert.ThrowsAsync<IOException>(teaching.ShutdownAsync);
            Assert.Same(light.OffFailure, failure);
            Assert.Contains(nameof(TestLight.TurnOff), failure.StackTrace);
            var deactivateFailure = new InvalidOperationException("Teaching deactivation failed.");
            teaching.CarrierImages = new List<CarrierImageTileView>();
            void FailDeactivation(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(TeachingViewModel.CarrierImages))
                    throw deactivateFailure;
            }

            teaching.PropertyChanged += FailDeactivation;
            try
            {
                var combined = await Assert.ThrowsAsync<AggregateException>(teaching.ShutdownAsync);
                Assert.Equal(
                    new Exception[] { deactivateFailure, light.OffFailure },
                    combined.Flatten().InnerExceptions);
            }
            finally
            {
                teaching.PropertyChanged -= FailDeactivation;
            }
            light.FailOff = false;
            await main.NavigateCommand.ExecuteAsync(AppPage.Settings);
            Assert.Equal(AppPage.Settings, main.SelectedPage);
            Assert.Null(main.NavigationError);

            var settings = services.GetRequiredService<SettingsViewModel>();
            var lightTest = settings.TestLightCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(() => settings.LightTestOn, TimeSpan.FromSeconds(2)));
            releaseStop.Reset();
            stopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var returning = main.NavigateCommand.ExecuteAsync(AppPage.Teaching);
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
            await main.NavigateCommand.ExecuteAsync(AppPage.Teaching);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            Assert.True(light.IsOn);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);

            var live = new CheckBox();
            live.SetBinding(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding("Inspection.IsLiveView")
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
                Assert.True(machine.IsResetAllowed);
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
            reference.LowerRightLocatingPin = new() { X = 1, Y = 1 };
            await services.GetRequiredService<InspectionStation>().HomeHorizontalAsync();
            teaching.RecipeEditor.Name = "ThreadingScan";
            teaching.AddBoltPointCommand.Execute(null);
            var firstBolt = teaching.SelectedPoint!;
            // Device failures are reported at the teaching command boundary.
            light.BeforeOn = () => throw new InvalidOperationException("Scan light ON failed.");
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
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
            var scan = teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(liveButton.IsEnabled);
            Assert.False(teaching.ToggleLiveViewCommand.CanExecute(null));
            Assert.False(teaching.Inspection.IsLiveView);
            teaching.TeachCurrentPositionCommand.Cancel();
            releaseStop.Set();
            await scan.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(teaching.CameraError);
            Assert.True(liveButton.IsEnabled);

            // A committed record stays with its original point even if selection changes during notification.
            light.BeforeOn = null;
            var next = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.DataMatrix);
            void SelectNextOnSave(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(RecipeEditor.ActiveName))
                    teaching.SelectedPoint = next;
            }
            teaching.RecipeEditor.PropertyChanged += SelectNextOnSave;
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            teaching.RecipeEditor.PropertyChanged -= SelectNextOnSave;
            Assert.Same(next, teaching.SelectedPoint);
            Assert.Single(teaching.CarrierImages);
            Assert.Null(VirtualTest.RecordedImage(teaching));
            teaching.SelectedPoint = firstBolt;
            var firstImage = VirtualTest.RecordedImage(teaching)!;
            Assert.True(firstImage.Image.IsFrozen);
            // Only Record Position replaces the selected point's image, capture XY and bolt coordinates.
            var gantry = services.GetRequiredService<InspectionStation>();
            await gantry.MoveAxisAsync(MotionAxis.X, 10, 10_000);
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            var liveLightOnCalls = light.OnCalls;
            var liveLightOffCalls = light.OffCalls;
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Null(teaching.CameraError);
            Assert.True(teaching.Inspection.IsLiveView);
            Assert.True(light.IsOn);
            // Capture reapplies the selected light level without stopping Live or turning light off.
            Assert.Equal(liveLightOnCalls + 1, light.OnCalls);
            Assert.Equal(liveLightOffCalls, light.OffCalls);
            Assert.Single(teaching.CarrierImages);
            var recordedImage = VirtualTest.RecordedImage(teaching)!;
            Assert.NotSame(firstImage.Image, recordedImage.Image);
            Assert.Equal(firstImage.Metadata.Number, recordedImage.Metadata.Number);
            Assert.Equal(10, recordedImage.Position!.X);
            Assert.Equal(firstImage.Metadata.Region, recordedImage.Metadata.Region);
            Assert.Equal(10, firstBolt.Position.Bolt!.X);
            var firstMetadata = recordedImage.Metadata;

            // The other heat sink and Data Matrix each own one independent image.
            teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
            teaching.AddBoltPointCommand.Execute(null);
            var secondBolt = teaching.SelectedPoint!;
            Assert.Null(VirtualTest.RecordedImage(teaching));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(2, teaching.CarrierImages.Count);
            Assert.True(teaching.Inspection.HasPosition(firstBolt.Position.Bolt));
            Assert.True(teaching.Inspection.HasPosition(secondBolt.Position.Bolt!));
            teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
            teaching.SelectedPoint = firstBolt;
            Assert.Same(firstMetadata, VirtualTest.RecordedImage(teaching)!.Metadata);
            foreach (var heatSink in Enum.GetValues<HeatSinkSlot>())
            {
                teaching.SelectedPcb = heatSink;
                teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.DataMatrix);
                Assert.Null(VirtualTest.RecordedImage(teaching));
                await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
                Assert.True(teaching.Inspection.HasBarcodeRegion(heatSink));
            }
            Assert.Equal(4, teaching.CarrierImages.Count);
            // Selecting another point stops Live before applying its saved light level.
            Assert.False(teaching.Inspection.IsLiveView);

            var originalName = teaching.RecipeEditor.ActiveName;
            teaching.RecipeEditor.Name = "ThreadingScanCopy";
            await teaching.RecipeEditor.SaveAsync();
            await teaching.RecipeEditor.LoadCommand.ExecuteAsync("ThreadingScanCopy");
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => teaching.CarrierImages.Count == 4, TimeSpan.FromSeconds(2)));
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.DataMatrix);
            var unchanged = teaching.CarrierImages.Where(image => image != VirtualTest.RecordedImage(teaching)).ToArray();
            await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Null(teaching.CameraError);
            Assert.Null(teaching.RecipeEditor.Error);
            Assert.Equal(4, teaching.CarrierImages.Count);
            Assert.All(unchanged, image => Assert.Contains(image, teaching.CarrierImages));
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
                var previous = VirtualTest.RecordedImage(teaching);
                var capture = teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
                await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var closing = closeTeaching ? teaching.ShutdownAsync() : Task.CompletedTask;
                if (!closeTeaching)
                    io.SetInput(InputIo.AutoMode, false);
                releaseStop.Set();
                await Task.WhenAll(capture, closing).WaitAsync(TimeSpan.FromSeconds(2));
                var saved = services.GetRequiredService<MachineStore>().LoadRecipe(teaching.RecipeEditor.ActiveName);
                Assert.Equal(4, saved.CarrierImages.Count);
                Assert.Equivalent(previous!.Metadata, saved.CarrierImages.Single(image => image.Number == previous.Metadata.Number));
                Assert.Null(teaching.CameraError);
                Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
                light.BeforeOn = null;
                io.SetInput(InputIo.AutoMode, true);
                teaching.Activate();
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => teaching.CarrierImages.Count == 4 && teaching.TeachCurrentPositionCommand.CanExecute(null),
                    TimeSpan.FromSeconds(2)));
            }
            var originalRecipe = services.GetRequiredService<MachineStore>().LoadRecipe(originalName);
            Assert.Equal(4, originalRecipe.CarrierImages.Count);
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
        var log = new ApplicationLog();
        using var loggerFactory = log.CreateLoggerFactory();
        var logger = loggerFactory.CreateLogger<OutputWindowThreadingTests>();
        logger.LogInformation("Before opening logs");
        var window = new LogWindow(new LogWindowViewModel(log));
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

            await Task.Run(() => logger.LogInformation("Newest background entry"));
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => text.Text.Contains("Newest background entry"),
                TimeSpan.FromSeconds(2)));
            Assert.StartsWith(log.Snapshot().Last().Text, text.Text);

            model.IsPaused = true;
            var paused = text.Text;
            text.SelectAll();
            Assert.Equal(paused, text.SelectedText);
            Assert.Contains(Environment.NewLine, text.SelectedText);
            await Task.Run(() => logger.LogInformation("Entry while paused"));
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
            await Task.Run(() => logger.LogInformation("Entry after clear"));
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
        await Task.Run(() => logger.LogInformation("After closing logs"));
        Assert.Equal(0, notificationsAfterClose);
        var reopened = new LogWindow(new LogWindowViewModel(log));
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

        var stopper = new OutputWindowRow(signals.Outputs[OutputIo.PcbPlacementStopperUp], machine);
        var feedback = BindOutputRow(window, stopper).Feedback;
        await Task.Run(
            () =>
            {
                io.SetOutput(OutputIo.PcbPlacementStopperUp, true);
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
        Assert.False(io.GetOutput(stopper.Io.Signal));
        Assert.True(stopper.ToggleCommand.CanExecute(null));
        stopper.ToggleCommand.Execute(null);
        Assert.True(io.GetOutput(stopper.Io.Signal));
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

    private sealed class TestLight : ILightController
    {
        public TestLight()
        {
            OffFailure = new("Simulated live light OFF failure.");
        }

        public Action? BeforeOn { get; set; }
        public Action? BeforeOff { get; set; }
        public int OnCalls { get; private set; }
        public int OffCalls { get; private set; }
        public bool IsOn { get; private set; }
        public bool FailOff { get; set; }
        public IOException OffFailure { get; }

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
            OffCalls++;
            IsOn = false;
        }

        public void TurnOff(int channel)
        {
            OffCalls++;
            BeforeOff?.Invoke();
            if (FailOff)
                throw OffFailure;
            IsOn = false;
        }
    }
}
