using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;
using IBTM.Inspection.Training;

namespace IBTM.UI;

public enum AppPage
{
    [Description("Operation")]
    Operation,

    [Description("Supply Teaching")]
    SupplyTeaching,

    [Description("Station Teaching")]
    StationTeaching,

    [Description("Bolt Training")]
    BoltTraining,

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
    private readonly SupplyTeachingViewModel _supplyTeachingViewModel;
    private readonly StationTeachingViewModel _stationTeachingViewModel;
    private readonly BoltTrainingViewModel _boltTrainingViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ManualHardwareViewModel _manualHardwareViewModel;
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private readonly IAsyncRelayCommand[] _recipeEditingCommands;
    private int _stateRefreshQueued;
    private bool _shuttingDown;

    [ObservableProperty]
    private string? _resetError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    [NotifyPropertyChangedFor(nameof(CurrentPageEnabled))]
    [NotifyPropertyChangedFor(nameof(RecipeToolsVisible))]
    [NotifyPropertyChangedFor(nameof(OperationPageSelected))]
    private AppPage _selectedPage = AppPage.Operation;

    public MainViewModel(
        OperationViewModel operationViewModel,
        SupplyTeachingViewModel supplyTeachingViewModel,
        StationTeachingViewModel stationTeachingViewModel,
        BoltTrainingViewModel boltTrainingViewModel,
        BoltImageCollector imageCollector,
        SettingsViewModel settingsViewModel,
        ManualHardwareViewModel manualHardwareViewModel,
        RecipeEditor recipeEditor,
        MachineState state,
        UnitSettings units,
        DriverSettings drivers,
        MachineController machine)
    {
        Operation = operationViewModel;
        _supplyTeachingViewModel = supplyTeachingViewModel;
        _stationTeachingViewModel = stationTeachingViewModel;
        _boltTrainingViewModel = boltTrainingViewModel;
        ImageCollector = imageCollector;
        _settingsViewModel = settingsViewModel;
        _manualHardwareViewModel = manualHardwareViewModel;
        RecipeEditor = recipeEditor;
        _state = state;
        _machine = machine;
        var controlVirtual = drivers.Control == ControlDriver.Virtual;
        var cameraVirtual = drivers.Camera == CameraDriver.Virtual;
        var boltVirtual = drivers.Bolt == BoltDriver.Virtual;
        var inspectionSimulated = units.Inspection && drivers.Inspection == InspectionAlgorithm.Virtual;
        Environment = (controlVirtual, cameraVirtual, boltVirtual) switch
        {
            (true, true, true) => MachineEnvironmentDisplay.Virtual,
            (false, false, false) when !inspectionSimulated => MachineEnvironmentDisplay.Physical,
            _ => MachineEnvironmentDisplay.Mixed,
        };
        _recipeEditingCommands = [
            recipeEditor.SaveCommand,
            recipeEditor.LoadCommand,
            supplyTeachingViewModel.TeachCurrentPositionCommand,
            supplyTeachingViewModel.MoveToPointCommand,
            supplyTeachingViewModel.SetOutputOnCommand,
            supplyTeachingViewModel.SetOutputOffCommand,
            stationTeachingViewModel.TeachCurrentPositionCommand,
            stationTeachingViewModel.TeachImagePointCommand,
            stationTeachingViewModel.MoveToPointCommand,
            stationTeachingViewModel.ReturnFromPickupCommand,
            stationTeachingViewModel.SetOutputOnCommand,
            stationTeachingViewModel.SetOutputOffCommand,
            stationTeachingViewModel.CaptureCarrierImagesCommand,
            stationTeachingViewModel.CaptureInspectionCommand,
            stationTeachingViewModel.ReinspectImageCommand,
            stationTeachingViewModel.CollectBoltImagesCommand,
            stationTeachingViewModel.TeachImageRegionCommand,
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
    public BoltImageCollector ImageCollector { get; }
    public MachineEnvironmentDisplay Environment { get; }

    public bool RecipeToolsVisible
    {
        get
        {
            return SelectedPage is AppPage.SupplyTeaching or AppPage.StationTeaching;
        }
    }

    public bool RecipeEditingEnabled
    {
        get
        {
            return _state.ManualMode
                && !_state.IsRunning
                && Array.TrueForAll(_recipeEditingCommands, static command => !command.IsRunning);
        }
    }

    public bool OperationPageSelected
    {
        get
        {
            return SelectedPage == AppPage.Operation;
        }
    }

    public ObservableObject CurrentPage
    {
        get
        {
            return SelectedPage switch
            {
                AppPage.Operation => Operation,
                AppPage.SupplyTeaching => _supplyTeachingViewModel,
                AppPage.StationTeaching => _stationTeachingViewModel,
                AppPage.BoltTraining => _boltTrainingViewModel,
                AppPage.Settings => _settingsViewModel,
                AppPage.ManualHardware => _manualHardwareViewModel,
                _ => throw new ArgumentOutOfRangeException(nameof(SelectedPage)),
            };
        }
    }

    // Window access follows selector mode only, not alarm/busy output admission.
    public bool OutputsWindowEnabled
    {
        get
        {
            return !_shuttingDown && !_state.Display.AutoMode;
        }
    }

    public bool AdcProtocolEnabled
    {
        get
        {
            return !_shuttingDown;
        }
    }

    public bool CurrentPageEnabled
    {
        get
        {
            return SelectedPage is AppPage.Operation or AppPage.Settings or AppPage.ManualHardware
                || (!RecipeEditor.SaveCommand.IsRunning && !RecipeEditor.LoadCommand.IsRunning);
        }
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
            CommandShutdown.WaitAsync(CommandShutdown.Capture(ResetCommand)),
            Operation.ShutdownAsync(),
            _supplyTeachingViewModel.ShutdownAsync(),
            _stationTeachingViewModel.ShutdownAsync(),
            _manualHardwareViewModel.ShutdownAsync(),
            _boltTrainingViewModel.ShutdownAsync(),
            _settingsViewModel.ShutdownAsync(),
            RecipeEditor.ShutdownAsync());
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        // The controller rechecks the same conditions as the physical RESET input.
        if (!CanReset())
            return;
        ResetError = null;
        Trace.TraceInformation("On-screen RESET requested.");
        try
        {
            await Task.Run(_machine.ResetAsync);
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
        finally
        {
            ResetCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanReset()
    {
        return !_shuttingDown && _machine.CanReset;
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(AppPage page)
    {
        SelectedPage = page;
    }

    partial void OnSelectedPageChanging(AppPage value)
    {
        DeactivateCurrentPage();
    }

    partial void OnSelectedPageChanged(AppPage value)
    {
        ActivateCurrentPage();
        if (value == AppPage.Operation)
        {
            _state.Refresh();
        }
    }

    private bool CanNavigate(AppPage page)
    {
        return page == AppPage.Operation
            || !_shuttingDown
            && !_state.AutomaticRunning
            && page switch
            {
                AppPage.Settings or AppPage.ManualHardware => true,
                AppPage.BoltTraining or AppPage.SupplyTeaching or AppPage.StationTeaching => _state.ManualMode,
                _ => false,
            };
    }

    private void ActivateCurrentPage()
    {
        switch (SelectedPage)
        {
            case AppPage.Operation:
                Operation.Activate();
                break;
            case AppPage.SupplyTeaching:
                _supplyTeachingViewModel.Activate();
                break;
            case AppPage.StationTeaching:
                _stationTeachingViewModel.Activate();
                break;
            case AppPage.ManualHardware:
                _manualHardwareViewModel.Activate();
                break;
            case AppPage.BoltTraining:
                _boltTrainingViewModel.Activate();
                break;
            case AppPage.Settings:
                _settingsViewModel.RefreshCommands();
                break;
        }
    }

    private void DeactivateCurrentPage()
    {
        switch (SelectedPage)
        {
            case AppPage.Operation:
                Operation.Deactivate();
                break;
            case AppPage.SupplyTeaching:
                _supplyTeachingViewModel.Deactivate();
                break;
            case AppPage.StationTeaching:
                _stationTeachingViewModel.Deactivate();
                break;
            case AppPage.ManualHardware:
                _manualHardwareViewModel.Deactivate();
                break;
            case AppPage.BoltTraining:
                _boltTrainingViewModel.Deactivate();
                break;
            case AppPage.Settings:
                _settingsViewModel.TestLightCommand.Cancel();
                break;
        }
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
                NavigateCommand.NotifyCanExecuteChanged();
                ResetCommand.NotifyCanExecuteChanged();
                if (_state.AutomaticRunning && SelectedPage != AppPage.Operation)
                {
                    Navigate(AppPage.Operation);
                }
                else if (!_state.ManualMode
                    && SelectedPage is AppPage.SupplyTeaching or AppPage.StationTeaching)
                {
                    Navigate(AppPage.Operation);
                }
                else if (!_state.ManualMode
                    && SelectedPage == AppPage.BoltTraining
                    && !_boltTrainingViewModel.IsBusy)
                {
                    Navigate(AppPage.Operation);
                }

                if (SelectedPage == AppPage.Settings)
                    _settingsViewModel.RefreshCommands();
            });
    }

}
