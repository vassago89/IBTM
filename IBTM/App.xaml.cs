using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IBTM;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private ServiceProvider? _serviceProvider;
    private ILogger<App>? _log;
    private ILoggerFactory? _loggerFactory;
    private ApplicationTraceListener? _traceListener;
    private IDisposable? _camera;
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

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        var services = new ServiceCollection();
        try
        {
            var database = await Task.Run(() => new MachineStore());
            if (DevelopmentProfile.IsEnabled)
            {
                await DevelopmentProfile.PrepareAsync(database);
            }

            var settings = await MachineSettings.LoadAsync(database);
            var applicationLog = InitializeLogging(settings.Logging);
            services
                .AddSingleton(applicationLog)
                .AddSingleton(_loggerFactory);
            if (DevelopmentProfile.IsEnabled)
            {
                DevelopmentProfile.UseVirtualHardware(settings);
            }

            var recipes = new RecipeManager(database, settings.RecipeSelection);
            if (settings.RecipeSelection.LastRecipeName is { } recipeName)
                await recipes.LoadAsync(recipeName);
            _log.LogInformation(
                "{Message}",
                $"Settings loaded: {database.DatabaseFile}. Control={settings.Drivers.Control}, Camera={settings.Drivers.Camera}, Light={settings.Drivers.Light}, Bolt={settings.Drivers.Bolt}.");
            _log.LogInformation(
                "{Message}",
                $"Connections: AlphaMotion card={settings.AlphaMotion.ControllerNumber}, DI/DO counts detected during initialization; AJIN AxlOpen, interrupt={settings.Ajin.InterruptNumber}, input modules=[{string.Join(
                        ",",
                        settings.Ajin.RtexInputModules ?? [])}], output modules=[{string.Join(
                            ",",
                            settings.Ajin.RtexOutputModules ?? [])}], no .mot file loaded.");

            services
                .AddSingleton(database)
                .AddSingleton(recipes)
                .AddIbtmApplication(settings);
        }
        catch (Exception exception)
        {
            if (_log is null)
                InitializeLogging(new LogSettings());
            _log.LogError(exception, "Startup configuration failed. Hardware was not initialized.");
            MessageBox.Show(
                $"Startup configuration could not be prepared. Hardware was not initialized.\n\n{exception.GetBaseException().Message}",
                "Startup Configuration Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _exitCode = 1;
            await CompleteExitAsync();
            Shutdown();
            return;
        }

        var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, });
        _serviceProvider = serviceProvider;
        _camera = serviceProvider.GetRequiredService<ICamera>() as IDisposable;

        await serviceProvider.GetRequiredService<MachineController>().InitializeAsync();
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        _log.LogInformation("Main window opened.");
    }

    [MemberNotNull(nameof(_log), nameof(_loggerFactory))]
    private ApplicationLog InitializeLogging(LogSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Directory) || !Path.IsPathFullyQualified(settings.Directory))
            throw new InvalidOperationException("Choose an absolute folder path for logs.");
        var logDirectory = Path.GetFullPath(settings.Directory);
        var applicationLog = new ApplicationLog(
            Path.Combine(logDirectory, "IBTM-.log"),
            Path.Combine(logDirectory, "Communication", "IBTM-.log"),
            settings.RetentionDays);
        _loggerFactory = applicationLog.CreateLoggerFactory();
        _log = _loggerFactory.CreateLogger<App>();
        _traceListener = new ApplicationTraceListener(_loggerFactory.CreateLogger<ApplicationTraceListener>());
        Trace.Listeners.Add(_traceListener);
        _log.LogInformation("Application starting. Base directory: {Directory}", AppContext.BaseDirectory);
        _log.LogInformation("Log folder: {Directory}; retention: {Days} days.", logDirectory, settings.RetentionDays);
        return applicationLog;
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
                _log?.LogError(exception, "Final device STOP failed during application exit.");
                _exitCode = 1;
                ShowError("Device STOP failed during application exit.", exception);
            }

            // Release the MVS connection even if a later DI-owned service fails to dispose.
            await Task.Run(DisposeCamera);

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
                _log?.LogError(exception, "Device disposal failed during application exit.");
                _exitCode = 1;
                ShowError("Device cleanup failed during application exit.", exception);
            }

            _serviceProvider = null;
            _log?.LogInformation("{Message}", _exitCode == 0
                ? "Application stopped."
                : "Application exited with shutdown errors. See preceding errors for unconfirmed device cleanup.");
        }
        finally
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            if (_traceListener is not null)
                Trace.Listeners.Remove(_traceListener);
            _traceListener?.Dispose();
            if (_loggerFactory is not null)
                await Task.Run(_loggerFactory.Dispose);
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
            _log?.LogError(exception, "Final device STOP failed during application exit.");
            _exitCode = 1;
            ShowError("Device STOP failed during application exit.", exception);
        }
        finally
        {
            // Application.Shutdown / session exit can bypass the awaited window-close path.
            DisposeCamera();
            e.ApplicationExitCode = _exitCode;
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }

    private void DisposeCamera()
    {
        if (_camera is not { } camera)
            return;

        try
        {
            camera.Dispose();
            _camera = null;
            _log?.LogInformation("Inspection camera disconnected and disposed.");
        }
        catch (Exception exception)
        {
            _log?.LogError(exception, "Camera disconnection failed during application exit.");
            _exitCode = 1;
            ShowError("Camera disconnection failed during application exit.", exception);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Unhandled UI exception.");
        ShowError("An unhandled UI error occurred.", e.Exception);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.LogError(e.ExceptionObject as Exception, "{Message}", $"Unhandled exception. Terminating={e.IsTerminating}.");
        ShowError(
            e.IsTerminating
                ? "An unhandled error occurred. The application will close."
                : "An unhandled application error occurred.",
            e.ExceptionObject);
        if (e.IsTerminating)
        {
            DisposeCamera();
            _loggerFactory?.Dispose();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Unobserved background task exception.");
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
}
