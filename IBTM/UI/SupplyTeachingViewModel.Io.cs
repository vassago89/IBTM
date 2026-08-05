using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel
{
    public string SupplyRotationLabel =>
        $"Supply Rotation  {_supplyHandler.Rotation.GetDescription()}";
    public string SupplyGripperLabel =>
        _supplyHandler.GripperClosed
            ? "Supply Gripper  Closed"
            : "Supply Gripper  Open";
    public string PlacementGripperLabel =>
        _placementStation.GripperClosed
            ? "Placement Gripper  Closed"
            : "Placement Gripper  Open";
    public string SupplyPcbLabel =>
        _supplyHandler.PcbPresent
            ? "Supply PCB  Detected"
            : "Supply PCB  Empty";
    public string PlacementPcbLabel =>
        _placementStation.PcbPresent
            ? "Placement PCB  Detected"
            : "Placement PCB  Empty";

    [RelayCommand]
    private async Task ToggleActuatorAsync(
        OutputIo output,
        CancellationToken cancellationToken)
    {
        var value = !_io.GetOutput(output);
        try
        {
            await SetActuatorAsync(output, value, cancellationToken);
            StatusMessage =
                $"{output.GetDescription()} {(value ? "on" : "off")}";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Output command stopped";
        }
        catch (IoFeedbackTimeoutException exception)
        {
            StatusMessage = $"Alarm: {exception.Message}";
        }

        RefreshActuators();
    }

    private Task SetActuatorAsync(
        OutputIo output,
        bool value,
        CancellationToken cancellationToken) =>
        output switch
        {
            OutputIo.PcbSupplyRotate =>
                _supplyHandler.SetRotatedAsync(value, cancellationToken),
            OutputIo.PcbSupplyGripperClose =>
                _supplyHandler.SetGripperAsync(value, cancellationToken),
            OutputIo.PcbPlacementGripperClose =>
                _placementStation.SetGripperAsync(value, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        };

    private void RefreshActuators()
    {
        OnPropertyChanged(nameof(SupplyRotationLabel));
        OnPropertyChanged(nameof(SupplyGripperLabel));
        OnPropertyChanged(nameof(PlacementGripperLabel));
        OnPropertyChanged(nameof(SupplyPcbLabel));
        OnPropertyChanged(nameof(PlacementPcbLabel));
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.PcbSupplyRotated
            or InputIo.PcbSupplyUnrotated
            or InputIo.PcbSupplyGripperClosed
            or InputIo.PcbPlacementGripperClosed
            or InputIo.PcbSupplyPcbPresent
            or InputIo.PcbPlacementPcbPresent)
        {
            Application.Current.Dispatcher.BeginInvoke(RefreshActuators);
        }
    }
}
