using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using IBTM.UI;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private ServiceProvider? _serviceProvider;
    private ApplicationLog? _log;
    private ApplicationTraceListener? _traceListener;
    private IAdcBus? _adcBus;
    private object? _displayedError;
    private int _exitCode;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains(DevelopmentProfile.Argument) && !DevelopmentProfile.IsEnabled)
        {
            MessageBox.Show(
                "Select the Virtual build configuration for the Virtual launch profile.",
                "IBTM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        var instanceMutex = new Mutex(
            initiallyOwned: true,
            DevelopmentProfile.IsEnabled
                ? @"Global\IBTM.VirtualDevelopment"
                : @"Global\IBTM.Application",
            out var createdNew);
        if (!createdNew)
        {
            instanceMutex.Dispose();
            MessageBox.Show(
                "IBTM is already running.",
                "IBTM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _instanceMutex = instanceMutex;

        _log = new ApplicationLog(
            Path.Combine(
                AppContext.BaseDirectory,
                "Logs",
                $"IBTM-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log"));
        _traceListener = new ApplicationTraceListener(_log);
        Trace.Listeners.Add(_traceListener);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _log.Write($"Application starting. Base directory: {AppContext.BaseDirectory}");

        base.OnStartup(e);

        MachineStore database;
        RecipeManager recipes;
        MachineSettings settings;
        try
        {
            database = await Task.Run(() => new MachineStore());
            if (DevelopmentProfile.IsEnabled)
            {
                await DevelopmentProfile.PrepareAsync(database);
            }

            settings = await MachineSettings.LoadAsync(database);
            if (DevelopmentProfile.IsEnabled)
            {
                DevelopmentProfile.UseVirtualHardware(settings);
            }

            recipes = new RecipeManager(database, settings.RecipeSelection);
            if (settings.RecipeSelection.LastRecipeName is { } recipeName)
                await recipes.LoadAsync(recipeName);
            _log.Write(
                $"Settings loaded: {database.DatabaseFile}. Control={settings.Drivers.Control}, Camera={settings.Drivers.Camera}, Light={settings.Drivers.Light}, Bolt={settings.Drivers.Bolt}.");
            _log.Write(
                $"Connections: AlphaMotion card={settings.AlphaMotion.ControllerNumber}, DI/DO counts detected during initialization; AJIN AxlOpen, interrupt={settings.Ajin.InterruptNumber}, input modules=[{string.Join(
                        ",",
                        settings.Ajin.RtexInputModules ?? [])}], output modules=[{string.Join(
                            ",",
                            settings.Ajin.RtexOutputModules ?? [])}], no .mot file loaded.");
        }
        catch (System.Exception exception)
        {
            _log.Error("Database startup failed. Hardware was not initialized.", exception);
            MessageBox.Show(
                $"Machine settings or recipes could not be loaded. Hardware was not initialized.\n\n{exception.GetBaseException().Message}",
                "Database Startup Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _exitCode = 1;
            await CompleteExitAsync();
            Shutdown();
            return;
        }

        var services = new ServiceCollection().AddSingleton(_log)
            .AddSingleton(database)
            .AddSingleton(recipes)
            .AddIbtmApplication(settings);
        var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, });
        _serviceProvider = serviceProvider;
        _adcBus = serviceProvider.GetService<IAdcBus>();
        if (_adcBus is not null)
            _adcBus.FrameTransferred += OnAdcFrameTransferred;

        await serviceProvider.GetRequiredService<MachineController>().InitializeAsync();
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        _log.Write("Main window opened.");
    }

    internal async Task CompleteExitAsync()
    {
        try
        {
            try
            {
                if (_serviceProvider is { } services)
                    await services.GetRequiredService<MachineController>().StopAsync();
            }
            catch (Exception exception)
            {
                _log?.Error("Final device STOP failed during application exit.", exception);
                _exitCode = 1;
                ShowError("Device STOP failed during application exit.", exception);
            }

            try
            {
                if (_serviceProvider is { } services)
                {
                    // DI also owns synchronous SDK handles; dispose them off the dispatcher.
                    await Task.Run(async () => await services.DisposeAsync());
                }
            }
            catch (Exception exception)
            {
                _log?.Error("Device disposal failed during application exit.", exception);
                _exitCode = 1;
                ShowError("Device cleanup failed during application exit.", exception);
            }

            _serviceProvider = null;
            _log?.Write(_exitCode == 0
                ? "Application stopped."
                : "Application exited with shutdown errors. See preceding errors for unconfirmed device cleanup.");
        }
        finally
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            if (_adcBus is not null)
                _adcBus.FrameTransferred -= OnAdcFrameTransferred;
            if (_traceListener is not null)
                Trace.Listeners.Remove(_traceListener);
            _traceListener?.Dispose();
            if (_log is not null)
                await _log.DisposeAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Unexpected application exit still attempts a synchronous hardware STOP.
            _serviceProvider?.GetService<MachineController>()?.Stop();
        }
        catch (Exception exception)
        {
            _log?.Error("Final device STOP failed during application exit.", exception);
            _exitCode = 1;
            ShowError("Device STOP failed during application exit.", exception);
        }
        finally
        {
            e.ApplicationExitCode = _exitCode;
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception.", e.Exception);
        ShowError("An unhandled UI error occurred.", e.Exception);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.Error(
            $"Unhandled exception. Terminating={e.IsTerminating}.",
            e.ExceptionObject as Exception);
        ShowError(
            e.IsTerminating
                ? "An unhandled error occurred. The application will close."
                : "An unhandled application error occurred.",
            e.ExceptionObject);
        if (e.IsTerminating)
            _log?.Dispose();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("Unobserved background task exception.", e.Exception);
        _ = Dispatcher.InvokeAsync(
            () => ShowError("A background task failed.", e.Exception));
    }

    private void ShowError(string message, object error)
    {
        // A fatal UI exception can also reach AppDomain.UnhandledException.
        if (ReferenceEquals(Interlocked.Exchange(ref _displayedError, error), error))
            return;

        var detail = error is Exception exception
            ? exception.GetBaseException().Message
            : error.ToString();
        MessageBox.Show(
            $"{message}\n\n{detail}\n\nSee the application log for details.",
            "IBTM Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnAdcFrameTransferred(AdcFrameDirection direction, byte[] frame)
    {
        _log?.Write(
            $"ADC {(direction == AdcFrameDirection.Transmit ? "TX" : "RX RAW")} {Convert.ToHexString(
                    frame)}");
    }
}
