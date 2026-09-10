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
        RecipeStore store;
        MachineSettings settings;
        Recipe recipe;
        try
        {
            database = await Task.Run(
                () =>
                {
                    MachineStore.RestorePending();
                    var value = new MachineStore();
                    LegacyMachineImport.Run(value, System.AppContext.BaseDirectory);
                    return value;
                });
            store = new RecipeStore(database);
            if (DevelopmentProfile.IsEnabled)
            {
                await DevelopmentProfile.PrepareAsync(store, database);
            }

            settings = await MachineSettings.LoadAsync(database);
            if (DevelopmentProfile.IsEnabled)
            {
                DevelopmentProfile.UseVirtualHardware(settings);
            }

            recipe = settings.RecipeSelection.LastRecipeName is { } recipeName
                ? await store.LoadRecipeAsync(recipeName)
                : new Recipe();
            _log.Write(
                $"Settings loaded: {database.DatabaseFile}. Control={settings.Drivers.Control}, Camera={settings.Drivers.Camera}, Light={settings.Drivers.Light}, Bolt={settings.Drivers.Bolt}.");
            _log.Write(
                $"Connections: AlphaMotion card={settings.AlphaMotion.ControllerNumber}, DI/DO counts detected during initialization; AJIN AxlOpenNoReset, interrupt={settings.Ajin.InterruptNumber}, input modules=[{string.Join(
                    ",",
                    settings.Ajin.RtexInputModules ?? [])}], output modules=[{string.Join(
                        ",",
                        settings.Ajin.RtexOutputModules ?? [])}], .mot loading disabled.");
        }
        catch (System.Exception exception)
        {
            _log.Error("Database startup failed. Hardware was not initialized.", exception);
            MessageBox.Show(
                $"Machine settings or recipes could not be loaded. Hardware was not initialized.\n\n{exception.GetBaseException().Message}",
                "Database Startup Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var services = new ServiceCollection().AddSingleton(_log)
            .AddSingleton(database)
            .AddSingleton(store)
            .AddIbtmApplication(settings, recipe);
        var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, });
        _serviceProvider = serviceProvider;
        _adcBus = serviceProvider.GetRequiredService<IAdcBus>();
        _adcBus.FrameTransferred += OnAdcFrameTransferred;

        await serviceProvider.GetRequiredService<MachineController>().InitializeAsync();
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        _log.Write("Main window opened.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _serviceProvider?.GetService<MachineController>()?.Stop();
            _serviceProvider?.Dispose();
            _log?.Write("Application stopped.");
        }
        catch (Exception exception)
        {
            _log?.Error("Application shutdown failed.", exception);
            throw;
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
            _log?.Dispose();
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
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.Error(
            $"Unhandled exception. Terminating={e.IsTerminating}.",
            e.ExceptionObject as Exception);
        if (e.IsTerminating)
            _log?.Dispose();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("Unobserved background task exception.", e.Exception);
    }

    private void OnAdcFrameTransferred(AdcFrameDirection direction, byte[] frame)
    {
        _log?.Write(
            $"ADC {(direction == AdcFrameDirection.Transmit ? "TX" : "RX RAW")} {Convert.ToHexString(
                frame)}");
    }

}
