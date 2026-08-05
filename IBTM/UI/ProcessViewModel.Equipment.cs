using System;
using System.Windows;
using IBTM.Device;
using IBTM.PcbSupply;

namespace IBTM.UI;

public partial class ProcessViewModel
{
    private static string FormatPosition(double x, double y, double z) =>
        $"X {x:F3}   Y {y:F3}   Z {z:F3}";

    private static string FormatXzPosition(double x, double z) =>
        $"X {x:F3}   Z {z:F3}";

    private void OnPcbSupplyPositionChanged(double x, double y, double z)
    {
        RunOnUi(() =>
        {
            var position = (X: x, Y: y, Z: z);
            PcbSupplyPosition = FormatXzPosition(x, z);
            UpdateSupplyMap(position);
        });
    }

    private void OnPcbPlacementPositionChanged(double x, double y, double z)
    {
        RunOnUi(() =>
        {
            var position = (X: x, Y: y, Z: z);
            PcbPlacementPosition = FormatPosition(x, y, z);
            UpdatePlacementMap(position);
        });
    }

    private void OnBoltFasteningPositionChanged(double x, double y, double z)
    {
        RunOnUi(() =>
        {
            BoltFasteningPosition = FormatPosition(x, y, z);
            UpdateBoltMap((x, y, z));
        });
    }

    private void OnNgTransferPositionChanged(double x, double y, double z)
    {
        RunOnUi(() =>
        {
            NgTransferPosition = FormatPosition(x, y, z);
            UpdateNgTransferMap((x, y, z));
        });
    }

    private void OnInputChanged(InputIo _, bool __) =>
        RunOnUi(RefreshIo);

    private void OnOutputChanged(OutputIo _, bool __) =>
        RunOnUi(RefreshIo);

    private void OnEquipmentStateChanged() =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsHoming));
            OnPropertyChanged(nameof(ConveyorRunning));
            OnPropertyChanged(nameof(BufferOwner));
            OnPropertyChanged(nameof(PcbSupplyMoving));
            OnPropertyChanged(nameof(PcbPlacementMoving));
            OnPropertyChanged(nameof(BoltFasteningMoving));
            OnPropertyChanged(nameof(NgTransferMoving));
            OnPropertyChanged(nameof(NgCarrierCount));
            OnPropertyChanged(nameof(NgCarrierCapacity));
            OnPropertyChanged(nameof(NgCarrierFull));
            OnPropertyChanged(nameof(EmergencyStopReleased));
            OnPropertyChanged(nameof(DoorClosed));
            OnPropertyChanged(nameof(AirPressureOk));
            NotifyCanExecuteChanged();
            UpdateDisplayStates();
        });

    private void RefreshIo()
    {
        PcbSupplyPcbDetected =
            _state.SupplyPcbPresent;
        PcbPlacementPcbDetected =
            _state.PlacementPcbPresent;
        PcbSupplyGripperClosed =
            _io.GetInput(InputIo.PcbSupplyGripperClosed);
        PcbPlacementGripperClosed =
            _io.GetInput(InputIo.PcbPlacementGripperClosed);
        PcbSupplyRotated =
            _state.SupplyRotation == PcbSupplyRotation.Rotated;
        PcbSupplyUpstreamBoardAvailable =
            _state.SupplyCarrierAvailable;
        PcbBufferPcbPresent =
            _pcbBuffer.PcbPresent;
        PcbPlacementHousing1Present =
            _io.GetInput(InputIo.PcbPlacementHousing1Present);
        PcbPlacementHousing2Present =
            _io.GetInput(InputIo.PcbPlacementHousing2Present);
        BoltFasteningCarrierJigPresent =
            _state.BoltCarrierJigPresent;
        BoltFasteningHousing1Present =
            _io.GetInput(InputIo.BoltFasteningHousing1Present);
        BoltFasteningHousing2Present =
            _io.GetInput(InputIo.BoltFasteningHousing2Present);
        BoltFasteningStopperUp =
            _io.GetInput(InputIo.BoltFasteningStopperUp);
        BoltFasteningBackupPlateUp =
            _io.GetInput(InputIo.BoltFasteningBackupPlateUp);
        InspectionCarrierJigPresent =
            _state.InspectionCarrierJigPresent;
        InspectionHousing1Present =
            _io.GetInput(InputIo.InspectionHousing1Present);
        InspectionHousing2Present =
            _io.GetInput(InputIo.InspectionHousing2Present);
        InspectionStopperUp =
            _io.GetInput(InputIo.InspectionStopperUp);
        InspectionBackupPlateUp =
            _io.GetInput(InputIo.InspectionBackupPlateUp);
        InspectionGripperClosed =
            _io.GetInput(InputIo.InspectionGripperClosed);
        PcbPlacementStopperUp =
            _io.GetInput(InputIo.PcbPlacementStopperUp);
        PcbPlacementBackupPlateUp =
            _io.GetInput(InputIo.PcbPlacementBackupPlateUp);
        PcbPlacementLaserOutput =
            _io.GetOutput(OutputIo.PcbPlacementLaser);
        InspectionLaserOutput =
            _io.GetOutput(OutputIo.InspectionLaser);
    }

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);
}
