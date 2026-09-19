using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public enum AppPage
{
    [Description("Operation")]
    Operation,

    [Description("Teaching")]
    Teaching,

    [Description("Settings")]
    Settings,

    [Description("Manual Control")]
    ManualHardware,
}

public enum MachineEnvironmentDisplay
{
    [Description("Physical")]
    Physical,

    [Description("Mixed")]
    Mixed,

    [Description("Virtual")]
    Virtual,
}

public partial class MainViewModel : ObservableObject
{
    private readonly TeachingViewModel _teachingViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ManualHardwareViewModel _manualHardwareViewModel;
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly IAsyncRelayCommand[] _recipeEditingCommands;
    private int _stateRefreshQueued;
    private bool _shuttingDown;
    private readonly DiagnosticWindows _windows;
    private readonly ILogger<MainViewModel> _log;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ControlsEnabled), nameof(OutputsWindowEnabled))]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputsCommand))]
    public partial bool IsClosing { get; set; }
    [ObservableProperty]
    public partial string? CloseError { get; set; }
    [ObservableProperty]
    public partial string? SelectedRecipeFile { get; set; }

    [ObservableProperty]
    public partial string? ResetError { get; set; }

    [ObservableProperty]
    public partial string? NavigationError { get; set; }

    public MainViewModel(
        OperationViewModel operationViewModel,
        TeachingViewModel teachingViewModel,
        SettingsViewModel settingsViewModel,
        ManualHardwareViewModel manualHardwareViewModel,
        RecipeEditor recipeEditor,
        MachineState state,
        DriverSettings drivers,
        MachineController machine,
        DiagnosticWindows windows,
        ILogger<MainViewModel> log)
    {
        OpenInputsCommand = new RelayCommand(OpenInputs, () => IsOpenDiagnosticAllowed);
        OpenOutputsCommand = new RelayCommand(OpenOutputs, () => OutputsWindowEnabled);
        OpenMotionCommand = new RelayCommand(OpenMotion, () => IsOpenDiagnosticAllowed);
        OpenAdcProtocolCommand = new RelayCommand(OpenAdcProtocol, () => AdcProtocolEnabled);
        OpenLogsCommand = new RelayCommand(OpenLogs, () => IsOpenDiagnosticAllowed);
        ResetCommand = new AsyncRelayCommand(
            ResetAsync, () => IsResetAllowed, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        NavigateCommand = new AsyncRelayCommand<AppPage>(NavigateAsync, IsNavigateAllowed);

        Operation = operationViewModel;
        _teachingViewModel = teachingViewModel;
        _settingsViewModel = settingsViewModel;
        _manualHardwareViewModel = manualHardwareViewModel;
        RecipeEditor = recipeEditor;
        _state = state;
        _machine = machine;
        _windows = windows;
        _log = log;
        var controlVirtual = drivers.Control == ControlDriver.Virtual;
        var cameraVirtual = drivers.Camera == CameraDriver.Virtual;
        var boltVirtual = drivers.Bolt == BoltDriver.Virtual;
        Environment = (controlVirtual, cameraVirtual, boltVirtual) switch
        {
            (true, true, true) => MachineEnvironmentDisplay.Virtual,
            (false, false, false) => MachineEnvironmentDisplay.Physical,
            _ => MachineEnvironmentDisplay.Mixed,
        };
        _recipeEditingCommands = [
            NavigateCommand,
            recipeEditor.SaveCommand,
            recipeEditor.LoadCommand,
            teachingViewModel.SaveHandoffSetupCommand,
            teachingViewModel.TeachCurrentPositionCommand,
            teachingViewModel.MoveToPointCommand,
            teachingViewModel.ReturnFromPickupCommand,
            teachingViewModel.GrabCommand,
            teachingViewModel.ApplyRulerResolutionCommand,
            teachingViewModel.CaptureInspectionCommand,
            teachingViewModel.ReinspectImageCommand,
            teachingViewModel.DrawFovRegionCommand,
            teachingViewModel.TeachFovRegionCommand,
        ];
        foreach (var command in _recipeEditingCommands)
        {
            command.PropertyChanged += OnRecipeEditingChanged;
        }

        state.DisplayChanged += OnMachineStateChanged;
        ActivateCurrentPage();
    }

    public OperationViewModel Operation { get; }
    public RecipeEditor RecipeEditor { get; }
    public MachineEnvironmentDisplay Environment { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(CurrentPageEnabled))]
    [NotifyPropertyChangedFor(nameof(OperationPageSelected))]
    public partial AppPage SelectedPage { get; private set; } = AppPage.Operation;

    public bool RecipeEditingEnabled
    {
        get
        {
            return !_shuttingDown
                && !IsClosing
                && _state.SetupEditingEnabled
                && Array.TrueForAll(_recipeEditingCommands, static command => !command.IsRunning);
        }
    }

    public bool OperationPageSelected => SelectedPage == AppPage.Operation;

    public ObservableObject CurrentPage
    {
        get
        {
            switch (SelectedPage)
            {
                case AppPage.Operation:
                    return Operation;
                case AppPage.Teaching:
                    return _teachingViewModel;
                case AppPage.Settings:
                    return _settingsViewModel;
                case AppPage.ManualHardware:
                    return _manualHardwareViewModel;
                default:
                    throw new ArgumentOutOfRangeException(nameof(SelectedPage));
            }
        }
    }

    // Window access follows selector mode only, not alarm/busy output admission.
    public bool OutputsWindowEnabled => !_shuttingDown && !IsClosing && !_state.Display.AutoMode;

    public bool AdcProtocolEnabled => !_shuttingDown && _settingsViewModel.ActiveBoltDriver != BoltDriver.Io;

    public bool CurrentPageEnabled
    {
        get
        {
            return !NavigateCommand.IsRunning
                && (SelectedPage is AppPage.Operation or AppPage.Settings or AppPage.ManualHardware
                    || !RecipeEditor.SaveCommand.IsRunning && !RecipeEditor.LoadCommand.IsRunning);
        }
    }

    public bool ControlsEnabled => !IsClosing;

    partial void OnSelectedRecipeFileChanged(string? value)
    {
        if (value is null)
            return;
        // File selection is an action: clear it so the same recipe can be chosen again.
        SelectedRecipeFile = null;
        if (RecipeEditingEnabled)
            RecipeEditor.LoadCommand.Execute(value);
    }

    public IRelayCommand OpenInputsCommand { get; }

    private void OpenInputs()
    {
        _windows.OpenInputs();
    }

    public IRelayCommand OpenOutputsCommand { get; }

    private void OpenOutputs()
    {
        _windows.OpenOutputs();
    }

    public IRelayCommand OpenMotionCommand { get; }

    private void OpenMotion()
    {
        _windows.OpenMotion();
    }

    public IRelayCommand OpenAdcProtocolCommand { get; }

    private void OpenAdcProtocol()
    {
        _windows.OpenAdcProtocol();
    }

    public IRelayCommand OpenLogsCommand { get; }

    private void OpenLogs()
    {
        _windows.OpenLogs();
    }

    private bool IsOpenDiagnosticAllowed => !IsClosing && !_shuttingDown;

    public async Task<bool> TryCloseAsync()
    {
        IsClosing = true;
        CloseError = null;
        _windows.PrepareShutdown();
        try
        {
            await CommandShutdown.WaitAsync(
                _machine.ShutdownAsync(),
                _windows.ShutdownAsync(),
                ShutdownAsync());
            return true;
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "Main window shutdown failed.");
            var errors = exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.Select(error => error.Message).Distinct()
                : [exception.Message];
            CloseError = "Device stop or shutdown could not be confirmed.\n"
                + "Check that the equipment is safely stopped before exiting.\n\n"
                + string.Join("\n", errors)
                + "\n\nExit the application anyway? Full details are saved in the log.";
            IsClosing = false;
            return false;
        }
    }

    public void ApproveUnconfirmedExit()
    {
        IsClosing = true;
        _log.LogInformation("Operator approved application exit after shutdown failure; device stop is unconfirmed.");
    }

    public Task ShutdownAsync()
    {
        _shuttingDown = true;
        ResetCommand.NotifyCanExecuteChanged();
        _state.DisplayChanged -= OnMachineStateChanged;
        foreach (var command in _recipeEditingCommands)
        {
            command.PropertyChanged -= OnRecipeEditingChanged;
        }

        return Task.WhenAll(
            CommandShutdown.WaitAsync(CommandShutdown.Capture(ResetCommand, NavigateCommand)),
            Operation.ShutdownAsync(),
            _teachingViewModel.ShutdownAsync(),
            _manualHardwareViewModel.ShutdownAsync(),
            _settingsViewModel.ShutdownAsync(),
            RecipeEditor.ShutdownAsync());
    }

    public IAsyncRelayCommand ResetCommand { get; }

    private async Task ResetAsync()
    {
        // Acknowledge even when hardware recovery is blocked; the controller owns admission.
        ResetError = null;
        Trace.TraceInformation("On-screen RESET requested.");
        try
        {
            await _machine.ResetAsync();
        }
        catch (OperationCanceledException)
        {
            Trace.TraceInformation("On-screen RESET cancelled.");
        }
        catch (Exception exception)
        {
            ResetError = $"RESET failed: {exception.Message}";
            Trace.TraceError("On-screen RESET failed. {0}", exception);
        }
    }

    private bool IsResetAllowed => !_shuttingDown;

    public IAsyncRelayCommand<AppPage> NavigateCommand { get; }

    private async Task NavigateAsync(AppPage page)
    {
        if (page == SelectedPage)
            return;

        NavigationError = null;
        try
        {
            await DeactivateCurrentPageAsync();
            if (_shuttingDown)
                return;
            // The selector can change while the previous device operation is stopping.
            if (!IsNavigateAllowed(page))
                page = AppPage.Operation;

            SelectedPage = page;
            ActivateCurrentPage();
            if (page == AppPage.Operation)
                _state.Refresh();
        }
        catch (Exception exception)
        {
            NavigationError = $"Page change failed: {exception.Message}";
            Trace.TraceError("Page change failed while leaving {0}. {1}", SelectedPage, exception);
            if (!_shuttingDown)
                ActivateCurrentPage();
        }
    }

    private bool IsNavigateAllowed(AppPage page)
    {
        return !_shuttingDown
            && (page == AppPage.Operation
                || !_state.AutomaticRunning
                    && page switch
                    {
                        AppPage.Settings or AppPage.ManualHardware => true,
                        AppPage.Teaching => _state.ManualMode,
                        _ => false,
                    });
    }

    private void ActivateCurrentPage()
    {
        switch (SelectedPage)
        {
            case AppPage.Operation:
                Operation.Activate();
                break;
            case AppPage.Teaching:
                _teachingViewModel.Activate();
                break;
            case AppPage.Settings:
                _settingsViewModel.RefreshCommands();
                break;
        }
    }

    private Task DeactivateCurrentPageAsync()
    {
        switch (SelectedPage)
        {
            case AppPage.Operation:
                Operation.Deactivate();
                return Task.CompletedTask;
            case AppPage.Teaching:
                return _teachingViewModel.ShutdownAsync();
            case AppPage.Settings:
                return _settingsViewModel.ShutdownAsync();
        }

        return Task.CompletedTask;
    }

    private void OnRecipeEditingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
        {
            OnPropertyChanged(nameof(RecipeEditingEnabled));
            OnPropertyChanged(nameof(CurrentPageEnabled));
        }
    }

    private void OnMachineStateChanged()
    {
        if (_shuttingDown || Interlocked.Exchange(ref _stateRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(
            () =>
            {
                Interlocked.Exchange(ref _stateRefreshQueued, 0);
                if (_shuttingDown)
                {
                    return;
                }

                OnPropertyChanged(nameof(OutputsWindowEnabled));
                OnPropertyChanged(nameof(AdcProtocolEnabled));
                OnPropertyChanged(nameof(CurrentPageEnabled));
                OnPropertyChanged(nameof(RecipeEditingEnabled));
                OpenOutputsCommand.NotifyCanExecuteChanged();
                if (!OutputsWindowEnabled)
                    _windows.CloseOutputs();
                NavigateCommand.NotifyCanExecuteChanged();
                var showOperation = _state.AutomaticRunning && SelectedPage != AppPage.Operation
                    || !_state.ManualMode
                        && SelectedPage == AppPage.Teaching;
                if (showOperation
                    && NavigationError is null
                    && NavigateCommand.CanExecute(AppPage.Operation))
                {
                    NavigateCommand.Execute(AppPage.Operation);
                }

                if (SelectedPage == AppPage.Settings)
                    _settingsViewModel.RefreshCommands();
            });
    }
}
