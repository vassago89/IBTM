using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
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

    [Description("Inspection")]
    Inspection,

    [Description("Settings")]
    Settings,

    [Description("Manual")]
    ManualHardware,
}

public partial class MainViewModel : ObservableObject
{
    private readonly TeachingViewModel _teachingViewModel;
    private readonly InspectionTeachingViewModel _inspectionTeachingViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ManualHardwareViewModel _manualHardwareViewModel;
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private bool _shuttingDown;
    private readonly DiagnosticWindowManager _windows;
    private readonly ILogger<MainViewModel> _log;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenOutputsAllowed))]
    public partial bool IsClosing { get; set; }
    [ObservableProperty]
    public partial string? CloseError { get; set; }
    [ObservableProperty]
    public partial string? SelectedRecipeName { get; set; }

    [ObservableProperty]
    public partial string? ResetError { get; set; }

    [ObservableProperty]
    public partial string? NavigationError { get; set; }

    public MainViewModel(
        OperationViewModel operationViewModel,
        TeachingViewModel teachingViewModel,
        InspectionTeachingViewModel inspectionTeachingViewModel,
        SettingsViewModel settingsViewModel,
        ManualHardwareViewModel manualHardwareViewModel,
        RecipeEditorViewModel recipeEditor,
        MachineState state,
        MachineController machine,
        DiagnosticWindowManager windows,
        ILogger<MainViewModel> log)
    {
        OpenInputsCommand = new RelayCommand(windows.OpenInputs, () => IsOpenDiagnosticAllowed);
        OpenOutputsCommand = new RelayCommand(OpenOutputs);
        OpenMotionCommand = new RelayCommand(windows.OpenMotion, () => IsOpenDiagnosticAllowed);
        OpenAdcProtocolCommand = new RelayCommand(windows.OpenAdcProtocol, () => IsOpenAdcProtocolAllowed);
        OpenLogsCommand = new RelayCommand(windows.OpenLogs, () => IsOpenDiagnosticAllowed);
        ResetCommand = new AsyncRelayCommand(
            ResetAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        NavigateCommand = new AsyncRelayCommand<AppPage>(NavigateAsync);

        Operation = operationViewModel;
        _teachingViewModel = teachingViewModel;
        _inspectionTeachingViewModel = inspectionTeachingViewModel;
        _settingsViewModel = settingsViewModel;
        _manualHardwareViewModel = manualHardwareViewModel;
        RecipeEditor = recipeEditor;
        _state = state;
        _machine = machine;
        _windows = windows;
        _log = log;
        NavigateCommand.PropertyChanged += OnRecipeEditingChanged;
        recipeEditor.LoadCommand.PropertyChanged += OnRecipeEditingChanged;
        teachingViewModel.PropertyChanged += OnRecipeEditingChanged;

        state.PropertyChanged += OnMachineStateChanged;
        ActivateCurrentPage();
    }

    public OperationViewModel Operation { get; }
    public RecipeEditorViewModel RecipeEditor { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(CurrentPageEnabled))]
    public partial AppPage SelectedPage { get; private set; } = AppPage.Operation;

    public bool RecipeEditingEnabled
    {
        get
        {
            return !_shuttingDown
                && !IsClosing
                && _state.SetupEditingEnabled
                && !NavigateCommand.IsRunning
                && !RecipeEditor.LoadCommand.IsRunning
                && !_teachingViewModel.IsBusy;
        }
    }

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
                case AppPage.Inspection:
                    return _inspectionTeachingViewModel;
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
    public bool IsOpenOutputsAllowed => !_shuttingDown && !IsClosing && !_state.AutoMode;

    private bool IsOpenAdcProtocolAllowed => !_shuttingDown;

    public bool CurrentPageEnabled
    {
        get
        {
            return !NavigateCommand.IsRunning
                && (SelectedPage is AppPage.Operation or AppPage.Inspection or AppPage.Settings or AppPage.ManualHardware
                    || !RecipeEditor.LoadCommand.IsRunning);
        }
    }

    partial void OnSelectedRecipeNameChanged(string? value)
    {
        if (value is null)
            return;
        // Selection loads a recipe; clear it so the same name can be chosen again.
        SelectedRecipeName = null;
        if (RecipeEditingEnabled)
            RecipeEditor.LoadCommand.Execute(value);
    }

    public IRelayCommand OpenInputsCommand { get; }

    public IRelayCommand OpenOutputsCommand { get; }

    private void OpenOutputs()
    {
        if (IsOpenOutputsAllowed)
            _windows.OpenOutputs();
    }

    public IRelayCommand OpenMotionCommand { get; }

    public IRelayCommand OpenAdcProtocolCommand { get; }

    public IRelayCommand OpenLogsCommand { get; }

    private bool IsOpenDiagnosticAllowed => !IsClosing && !_shuttingDown;

    public async Task<bool> TryCloseAsync()
    {
        IsClosing = true;
        CloseError = null;
        var preparation = Task.CompletedTask;
        try
        {
            _windows.PrepareShutdown();
        }
        catch (Exception exception)
        {
            preparation = Task.FromException(exception);
        }
        try
        {
            await CommandShutdown.WaitAsync(
                preparation,
                _machine.ShutdownAsync(),
                _windows.ShutdownAsync(),
                ShutdownAsync());
            await _machine.PcbHistory.FlushAsync();
            return true;
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "Main window shutdown failed.");
            var errors = exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.Select(error => error.Message).Distinct()
                : [exception.Message];
            CloseError = UiText.Get("Shutdown could not be completed.\n")
                + UiText.Get("Check equipment stop and any unsaved PCB results before exiting.\n\n")
                + string.Join("\n", errors)
                + UiText.Get("\n\nExit the application anyway? Full details are saved in the log.");
            IsClosing = false;
            return false;
        }
    }

    public void ApproveUnconfirmedExit()
    {
        IsClosing = true;
        _log.LogInformation("Operator approved application exit after shutdown failure. See the preceding stop/save errors.");
    }

    public Task ShutdownAsync()
    {
        _shuttingDown = true;
        OnPropertyChanged(nameof(IsResetAllowed));
        _state.PropertyChanged -= OnMachineStateChanged;
        NavigateCommand.PropertyChanged -= OnRecipeEditingChanged;
        RecipeEditor.LoadCommand.PropertyChanged -= OnRecipeEditingChanged;
        _teachingViewModel.PropertyChanged -= OnRecipeEditingChanged;

        return Task.WhenAll(
            CommandShutdown.WaitAsync(CommandShutdown.Capture(ResetCommand, NavigateCommand)),
            Operation.ShutdownAsync(),
            _teachingViewModel.ShutdownAsync(),
            _inspectionTeachingViewModel.ShutdownAsync(),
            _manualHardwareViewModel.ShutdownAsync(),
            _settingsViewModel.ShutdownAsync(),
            RecipeEditor.ShutdownAsync());
    }

    public IAsyncRelayCommand ResetCommand { get; }

    private async Task ResetAsync()
    {
        if (!IsResetAllowed)
            return;
        // Acknowledge even when hardware recovery is blocked; the controller owns admission.
        ResetError = null;
        _log.LogInformation("On-screen RESET requested.");
        try
        {
            await _machine.ResetAsync();
        }
        catch (Exception exception)
        {
            ResetError = UiText.Format($"RESET failed: {exception.Message}");
            _log.LogError(exception, "On-screen RESET failed.");
        }
    }

    public bool IsResetAllowed => !_shuttingDown;

    public IAsyncRelayCommand<AppPage> NavigateCommand { get; }

    private async Task NavigateAsync(AppPage page)
    {
        if (page == SelectedPage || !IsNavigateAllowed(page))
            return;

        var started = Stopwatch.GetTimestamp();
        var previousPage = SelectedPage;
        _log.LogInformation("UI navigation: {From} -> {To}, begin.", previousPage, page);
        NavigationError = null;
        try
        {
            switch (SelectedPage)
            {
                case AppPage.Operation:
                    Operation.Deactivate();
                    break;
                case AppPage.Teaching:
                    await _teachingViewModel.ShutdownAsync();
                    break;
                case AppPage.Settings:
                    await _settingsViewModel.ShutdownAsync();
                    break;
            }
            _log.LogInformation("UI navigation {From} -> {To}: previous page stopped, elapsed={ElapsedMs:F1} ms.",
                previousPage, page, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (_shuttingDown)
                return;
            // The selector can change while the previous device operation is stopping.
            if (!IsNavigateAllowed(page))
                page = AppPage.Operation;

            var activateStarted = Stopwatch.GetTimestamp();
            SelectedPage = page;
            ActivateCurrentPage();
            _log.LogInformation("UI navigation {From} -> {To}: view model activated, activation={ActivationMs:F1} ms, total={ElapsedMs:F1} ms.",
                previousPage, page, Stopwatch.GetElapsedTime(activateStarted).TotalMilliseconds,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (page == AppPage.Operation)
                _state.Refresh();
        }
        catch (Exception exception)
        {
            NavigationError = UiText.Format($"Page change failed: {exception.Message}");
            _log.LogError(exception, "Page change failed while leaving {Page}.", SelectedPage);
            if (!_shuttingDown)
                ActivateCurrentPage();
        }
    }

    public bool IsNavigateAllowed(AppPage page)
    {
        return !_shuttingDown
            && (page is AppPage.Operation or AppPage.Inspection
                || !_state.AutomaticRunning
                    && page switch
                    {
                        AppPage.Settings or AppPage.ManualHardware => true,
                        AppPage.Teaching => !_state.AutoMode,
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
            case AppPage.Inspection:
                _inspectionTeachingViewModel.Activate();
                break;
            case AppPage.Settings:
                _settingsViewModel.RefreshCommands();
                break;
        }
    }

    private void OnRecipeEditingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAsyncRelayCommand.IsRunning) or nameof(TeachingViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(RecipeEditingEnabled));
            OnPropertyChanged(nameof(CurrentPageEnabled));
        }
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_shuttingDown)
            return;
        OnPropertyChanged(nameof(CurrentPageEnabled));
        OnPropertyChanged(nameof(RecipeEditingEnabled));
        OnPropertyChanged(nameof(IsOpenOutputsAllowed));
        if (SelectedPage == AppPage.Settings)
            _settingsViewModel.RefreshCommands();

        if (e.PropertyName is not (nameof(MachineState.AutoMode) or nameof(MachineState.AutomaticRunning) or null))
            return;
        // Window closing and page activation own WPF views/collections.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown)
                return;
            if (!IsOpenOutputsAllowed)
                _windows.CloseOutputs();
            var showOperation = _state.AutomaticRunning && SelectedPage is not (AppPage.Operation or AppPage.Inspection)
                || _state.AutoMode && SelectedPage == AppPage.Teaching;
            if (showOperation && NavigationError is null && NavigateCommand.CanExecute(AppPage.Operation))
                NavigateCommand.Execute(AppPage.Operation);
        });
    }
}
