using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using IBTM;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        foreach (var name in new[] { "AppStyles", "MachineStyles", "WorkpieceStyles", "IoWindowStyles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/IBTM;component/UI/{name}.xaml"),
            });
        app.Resources["AppIcon"] = new DrawingImage();
        var bindingErrors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.WriteLine(e.Exception);
            Environment.ExitCode = 1;
            e.Handled = true;
            app.Shutdown();
        };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await VerifyAsync(app); }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                Console.WriteLine("BINDING ERRORS: " + bindingErrors);
                app.Shutdown();
            }
        }));
        app.Run();
    }

    private static async Task VerifyAsync(Application app)
    {
        var settings = new MachineSettings
        {
            Drivers = new()
            {
                Control = ControlDriver.Virtual,
                Camera = CameraDriver.Virtual,
                Light = LightDriver.Virtual,
                Bolt = BoltDriver.Virtual,
                Inspection = InspectionAlgorithm.Virtual,
            },
            Units = new()
            {
                MainConveyor = true, NgCarrierTransfer = true, NgShuttle = true, NgConveyor = true,
                PcbSupply = false, PcbPlacement = false, BoltFastening = false, Inspection = false,
                PickupBoltFeeder = false, ShootingBoltFeeder = false,
            },
        };
        settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 10000;
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 20, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 150, Y = 20 };
        settings.NgCarrierTransfer.Speed = 10000;
        var database = new MachineStore(Path.Combine(Path.GetTempPath(), "IBTM-repeat-ui-" + Guid.NewGuid().ToString("N") + ".db"));
        var dbType = typeof(MachineStore).Assembly.GetType("IBTM.Storage.MachineDb")!;
        var options = dbType.GetMethod("CreateOptions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(
                null, new object[] { database.DatabaseFile });
        using (var db = (DbContext)Activator.CreateInstance(dbType, options)!)
            db.Database.Migrate();
        using var services = new ServiceCollection()
            .AddSingleton(database)
            .AddIbtmApplication(settings, new Recipe { Name = "Virtual Repeat" })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var window = services.GetRequiredService<MainWindow>();
        window.Title = "IBTM - VIRTUAL REPEAT VERIFICATION";
        app.MainWindow = window;
        await machine.InitializeAsync();
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Console.WriteLine($"VISIBLE WINDOW: {window.IsVisible}, {window.ActualWidth} x {window.ActualHeight}");
        var operation = services.GetRequiredService<OperationViewModel>();
        await WaitAsync(() => state.Display.CanHome);
        await InvokeAsync(window, operation.HomeCommand);
        await WaitAsync(() => state.Display.Homed && state.Display.SetupEditingEnabled);
        var repeat = (CheckBox)window.FindName("RepeatMode");
        ((IToggleProvider)new ToggleButtonAutomationPeer(repeat).GetPattern(PatternInterface.Toggle)).Toggle();
        await WaitAsync(() => state.RepeatEnabled);
        io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        io.SetInput(InputIo.AutoMode, false);
        await WaitAsync(() => state.Display.CanStart);
        var start = Buttons(window).Single(button => ReferenceEquals(button.Command, operation.StartCommand));
        await WaitAsync(() => start.IsEnabled);
        ((IInvokeProvider)new ButtonAutomationPeer(start).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitAsync(() => state.Display.RepeatCycles >= 2 || state.IsError, 25000);
        if (state.IsError)
            throw new InvalidOperationException(state.AlarmDetail);
        var stop = Buttons(window).Single(button => ReferenceEquals(button.Command, operation.StopCommand));
        await WaitAsync(() => stop.IsEnabled);
        ((IInvokeProvider)new ButtonAutomationPeer(stop).GetPattern(PatternInterface.Invoke)).Invoke();
        if (operation.StartCommand.ExecutionTask is { } running)
            await running;
        await WaitAsync(() => !state.Display.AutomaticRunning && !state.Display.IsRunning);
        window.UpdateLayout();
        Capture(window, @"C:\git\IBTM\.verification\repeat-ui.png");
        Console.WriteLine($"UI VERIFIED: cycles={state.Display.RepeatCycles}, phase={state.Display.RepeatPhase}, alarm={state.Alarm}, mainRun={io.GetOutput(OutputIo.MainConveyorRun)}, ngRun={io.GetOutput(OutputIo.NgConveyorRun)}");
        await Task.Delay(15000);
        await machine.ShutdownAsync();
        await services.GetRequiredService<MainViewModel>().ShutdownAsync();
        window.Close();
        await Task.Delay(250);
    }

    private static async Task InvokeAsync(Window window, IAsyncRelayCommand command)
    {
        var button = Buttons(window).Single(item => ReferenceEquals(item.Command, command));
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Yield();
        if (command.ExecutionTask is { } task)
            await task;
    }

    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        if (parent is Button button)
            yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(parent, i)))
                yield return child;
    }

    private static async Task WaitAsync(Func<bool> condition, int milliseconds = 5000)
    {
        using var stop = new CancellationTokenSource(milliseconds);
        while (!condition())
            await Task.Delay(20, stop.Token);
    }

    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
