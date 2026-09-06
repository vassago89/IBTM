using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM.UI;

public enum SupplyTeachingActuator
{
    [Description("Supply Flip")]
    SupplyFlip,

    [Description("Supply IPM Fixer")]
    SupplyIpmFixer,

    [Description("Placement IPM Gripper")]
    PlacementIpmGripper,
}

public partial class SupplyTeachingViewModel
{
    public PcbSupplyRotationState SupplyRotation => _supplyHandler.Rotation;
    public bool SupplyIpmFixed =>
        _supplyHandler.IpmFixer == PcbSupplyCylinderState.Forward;
    public bool PlacementIpmGripperClosed =>
        _placementHandler.IpmGripper == PlacementGripperState.Closed;
    public bool SupplyPcbDetected =>
        _supplyHandler.Pcb != PcbSupplyPcbState.None;
    public bool PlacementPcbDetected =>
        _placementHandler.Pcb != PlacementPcbState.None;

    [RelayCommand(CanExecute = nameof(CanToggleActuator))]
    private async Task ToggleActuatorAsync(
        SupplyTeachingActuator actuator,
        CancellationToken cancellationToken)
    {
        var value = actuator switch
        {
            SupplyTeachingActuator.SupplyFlip =>
                SupplyRotation != PcbSupplyRotationState.Rotated,
            SupplyTeachingActuator.SupplyIpmFixer => !SupplyIpmFixed,
            SupplyTeachingActuator.PlacementIpmGripper =>
                !PlacementIpmGripperClosed,
            _ => throw new ArgumentOutOfRangeException(nameof(actuator)),
        };
        try
        {
            using var operation = LinkMotion(cancellationToken);
            await SetActuatorAsync(actuator, value, operation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IoTimeoutException)
        {
            _state.SetError(
                actuator == SupplyTeachingActuator.PlacementIpmGripper
                    ? MachineAlarm.PcbPlacement
                    : MachineAlarm.PcbSupply);
        }
        finally
        {
            RefreshActuators();
        }
    }

    private bool CanToggleActuator(SupplyTeachingActuator actuator) =>
        CanUseHandler(
            actuator == SupplyTeachingActuator.PlacementIpmGripper
                ? MotionGroup.PcbPlacementHandler
                : MotionGroup.PcbSupply)
        && (actuator != SupplyTeachingActuator.SupplyFlip || !_buffer.SupplyInside);

    private Task SetActuatorAsync(
        SupplyTeachingActuator actuator,
        bool value,
        CancellationToken cancellationToken) =>
        actuator switch
        {
            SupplyTeachingActuator.SupplyFlip =>
                _supplyHandler.SetRotatedAsync(value, cancellationToken),
            SupplyTeachingActuator.SupplyIpmFixer =>
                _supplyHandler.SetIpmFixerAsync(value, cancellationToken),
            SupplyTeachingActuator.PlacementIpmGripper =>
                _placementHandler.SetIpmGripperAsync(value, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(actuator)),
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

    private void OnHandlerChanged()
    {
        if (!PositionUpdatesActive)
        {
            return;
        }

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (PositionUpdatesActive)
            {
                RefreshActuators();
            }
        });
    }
}
