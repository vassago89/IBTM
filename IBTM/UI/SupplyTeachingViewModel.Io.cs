using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel
{
    public PcbSupplyRotation SupplyRotation => _supplyHandler.Rotation;
    public bool SupplyIpmFixed =>
        _supplyHandler.IpmFixer == PcbSupplyCylinderState.Forward;
    public bool PlacementIpmGripperClosed =>
        _placementHandler.Gripper == PlacementGripperState.Closed;
    public bool SupplyPcbDetected =>
        _supplyHandler.Pcb != PcbSupplyPcbState.None;
    public bool PlacementPcbDetected =>
        _placementHandler.Pcb != PlacementPcbState.None;

    [RelayCommand(CanExecute = nameof(CanToggleActuator))]
    private async Task ToggleActuatorAsync(
        OutputIo output,
        CancellationToken cancellationToken)
    {
        var value = output switch
        {
            OutputIo.PcbSupplyRotate =>
                SupplyRotation != PcbSupplyRotation.Rotated,
            OutputIo.PcbSupplyIpmFixerForward => !SupplyIpmFixed,
            OutputIo.PcbPlacementIpmGripperClose =>
                !PlacementIpmGripperClosed,
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        };
        try
        {
            await SetActuatorAsync(output, value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IoTimeoutException)
        {
            _state.SetError(
                output == OutputIo.PcbPlacementIpmGripperClose
                    ? MachineAlarm.Placement
                    : MachineAlarm.Supply);
        }
        finally
        {
            RefreshActuators();
        }
    }

    private bool CanToggleActuator(OutputIo output) =>
        (output != OutputIo.PcbSupplyRotate || !_buffer.SupplyInside)
        && CanUseHandler(
            output == OutputIo.PcbPlacementIpmGripperClose
                ? MotionGroup.PcbPlacementHandler
                : MotionGroup.PcbSupply);

    private Task SetActuatorAsync(
        OutputIo output,
        bool value,
        CancellationToken cancellationToken) =>
        output switch
        {
            OutputIo.PcbSupplyRotate =>
                _supplyHandler.SetRotatedAsync(value, cancellationToken),
            OutputIo.PcbSupplyIpmFixerForward =>
                _supplyHandler.SetIpmFixerAsync(value, cancellationToken),
            OutputIo.PcbPlacementIpmGripperClose =>
                _placementHandler.SetIpmGripperAsync(value, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        };

    private void RefreshActuators()
    {
        OnPropertyChanged(nameof(SupplyRotation));
        OnPropertyChanged(nameof(SupplyIpmFixed));
        OnPropertyChanged(nameof(PlacementIpmGripperClosed));
        OnPropertyChanged(nameof(SupplyPcbDetected));
        OnPropertyChanged(nameof(PlacementPcbDetected));
        NotifyManualTeachingCommands();
    }

    private void OnHandlerChanged() =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            RefreshActuators);
}
