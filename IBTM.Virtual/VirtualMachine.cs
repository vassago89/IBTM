using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMachine
{
    private const int TransferDelayMilliseconds = 200;

    private readonly VirtualIoService _io;
    private readonly IReadOnlyList<VirtualMotionService> _motions;
    private readonly bool[] _supplyPcbs = new bool[2];
    private int _supplySmemaVersion;
    private int _mainConveyorTransferVersion;
    private int _placementVacuumVersion;
    private int _pickupVacuumVersion;
    private int _shootingVacuumVersion;
    private int _shootVersion;
    private int _linearFeederVersion;
    private int _ngConveyorVersion;
    private int? _supplyPickupSlot;
    private bool _supplyAtBuffer;
    private bool _supplyHoldingPcb;
    private bool _placementAtBuffer;
    private bool _placementHoldingPcb;
    private bool _inspectionAtNgPickup;
    private bool _inspectionAtNgShuttle;
    private bool _ngCarrierHeld;

    public VirtualMachine(
        VirtualIoService io,
        IReadOnlyList<VirtualMotionService> motions)
    {
        _io = io;
        _motions = motions;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
        io.OutputApplied += ApplyPhysicalOutput;
        io.SetInput(InputIo.ServoMainContactorOn, true);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
        if (EmergencyStopPressed
            || _io.GetInput(InputIo.AutoMode) && DoorOpen)
        {
            DropServoPower();
        }
    }

    private bool DoorOpen =>
        _io.GetInput(InputIo.Door1Open)
        || _io.GetInput(InputIo.Door2Open)
        || _io.GetInput(InputIo.Door3Open)
        || _io.GetInput(InputIo.Door4Open)
        || _io.GetInput(InputIo.Door5Open)
        || _io.GetInput(InputIo.Door6Open);

    private bool EmergencyStopPressed =>
        _io.GetInput(InputIo.EmergencyStop1Pressed)
        || _io.GetInput(InputIo.EmergencyStop2Pressed);

    private void OnInputChanged(InputIo input, bool value)
    {
        if (value
            && (IsEmergencyStop(input)
                || (input == InputIo.AutoMode || IsDoor(input))
                && _io.GetInput(InputIo.AutoMode)
                && DoorOpen))
        {
            DropServoPower();
            return;
        }

        if (input == InputIo.ResetButton
            && value
            && !EmergencyStopPressed
            && (!_io.GetInput(InputIo.AutoMode) || !DoorOpen))
        {
            RestoreServoPower();
        }
    }

    private void DropServoPower()
    {
        _io.SetInput(InputIo.ServoMainContactorOn, false);
        foreach (var motion in _motions)
        {
            foreach (var axis in motion.Axes)
            {
                motion.SetServo(axis, false);
                motion.SetAlarm(axis, true);
            }
        }
    }

    private void RestoreServoPower()
    {
        _io.SetInput(InputIo.ServoMainContactorOn, true);
        foreach (var motion in _motions)
        {
            foreach (var axis in motion.Axes)
            {
                motion.SetServo(axis, true);
            }
        }
    }

    private static bool IsDoor(InputIo input) =>
        input is InputIo.Door1Open
            or InputIo.Door2Open
            or InputIo.Door3Open
            or InputIo.Door4Open
            or InputIo.Door5Open
            or InputIo.Door6Open;

    private static bool IsEmergencyStop(InputIo input) =>
        input is InputIo.EmergencyStop1Pressed
            or InputIo.EmergencyStop2Pressed;

    public void UpdateSupplyPosition(
        double x,
        double y,
        double z,
        double carrierY,
        (double X, double Z) pcb1,
        (double X, double Z) pcb2,
        AxisPos buffer)
    {
        _supplyAtBuffer = IsAt(x, y, z, buffer);
        if (_supplyAtBuffer && _supplyHoldingPcb)
        {
            _io.SetInput(InputIo.PcbBufferPcbPresent, true);
        }

        if (_supplyHoldingPcb)
        {
            return;
        }

        _supplyPickupSlot = IsAt(x, y, z, pcb1.X, carrierY, pcb1.Z)
            ? 0
            : IsAt(x, y, z, pcb2.X, carrierY, pcb2.Z)
                ? 1
                : null;
        _io.SetInput(
            InputIo.PcbSupplyPcbDetected,
            _supplyPickupSlot is { } slot && _supplyPcbs[slot]);
    }

    public void UpdatePlacementPosition(
        double x,
        double y,
        double z,
        AxisPos bufferPosition)
    {
        var wasAtBuffer = _placementAtBuffer;
        _placementAtBuffer = IsAt(x, y, z, bufferPosition);
        if (wasAtBuffer && !_placementAtBuffer && _placementHoldingPcb)
        {
            _io.SetInput(InputIo.PcbBufferPcbPresent, false);
        }

        _io.SetInput(
            InputIo.PcbPlacementPcbDetected,
            _placementHoldingPcb
            || _placementAtBuffer
            && _io.GetInput(InputIo.PcbBufferPcbPresent));
    }

    public void UpdateInspectionPosition(
        double x,
        double y,
        AxisPos pickupPosition,
        AxisPos shuttlePosition)
    {
        _inspectionAtNgPickup = IsAt(x, y, pickupPosition);
        _inspectionAtNgShuttle = IsAt(x, y, shuttlePosition);
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output is OutputIo.MainConveyorReadyToFront2
            or OutputIo.MainConveyorAvailableToRear
            or OutputIo.MainConveyorRun)
        {
            var transferVersion = Interlocked.Increment(
                ref _mainConveyorTransferVersion);
            if (_io.GetOutput(OutputIo.MainConveyorRun))
            {
                _ = TransferMainCarrierAsync(transferVersion);
            }
            else if (_io.GetOutput(OutputIo.MainConveyorReadyToFront2)
                     && !_io.GetInput(
                         InputIo.MainConveyorAvailableFromFront2)
                     && !_io.GetInput(
                         InputIo.PcbPlacementCarrierPresent))
            {
                _ = PresentMainCarrierAsync(transferVersion);
            }
        }

        if (output == OutputIo.PcbPlacementVacuumEjector)
        {
            var vacuumVersion = Interlocked.Increment(
                ref _placementVacuumVersion);
            _ = ApplyPlacementVacuumAsync(value, vacuumVersion);
            return;
        }

        if (output == OutputIo.BoltHead1VacuumPump)
        {
            var vacuumVersion = Interlocked.Increment(
                ref _pickupVacuumVersion);
            _ = ApplyPickupVacuumAsync(value, vacuumVersion);
            return;
        }

        if (output == OutputIo.BoltHead2VacuumPump)
        {
            var vacuumVersion = Interlocked.Increment(
                ref _shootingVacuumVersion);
            if (!value)
            {
                _ = ReleaseShootingVacuumAsync(vacuumVersion);
            }

            return;
        }

        if (output == OutputIo.ShootBolt)
        {
            var shootVersion = Interlocked.Increment(ref _shootVersion);
            _ = ApplyShootAsync(value, shootVersion);
            return;
        }

        if (output == OutputIo.LinearFeederRunSignal)
        {
            var feederVersion = Interlocked.Increment(
                ref _linearFeederVersion);
            if (value)
            {
                _ = FeedLinearBoltAsync(feederVersion);
            }

            return;
        }

        if (output == OutputIo.NgConveyorRun)
        {
            var conveyorVersion = Interlocked.Increment(
                ref _ngConveyorVersion);
            if (value)
            {
                _ = TransferNgCarrierAsync(conveyorVersion);
            }

            return;
        }

        if (output != OutputIo.PcbSupplyReadyToFront1)
        {
            return;
        }

        var version = Interlocked.Increment(ref _supplySmemaVersion);
        if (value)
        {
            _ = TransferSupplyCarrierAsync(version);
        }
    }

    private async Task ApplyPlacementVacuumAsync(bool value, int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_placementVacuumVersion != version)
        {
            return;
        }

        _io.SetInput(
            InputIo.PcbPlacementVacuumDetected,
            value
            && _placementAtBuffer
            && _io.GetInput(InputIo.PcbBufferPcbPresent));
    }

    private async Task ApplyPickupVacuumAsync(bool value, int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_pickupVacuumVersion != version)
        {
            return;
        }

        var loaded = value
            && _io.GetInput(InputIo.PickupFeederBoltDetected);
        _io.SetInput(InputIo.BoltHead1VacuumDetected, loaded);
        if (!loaded)
        {
            return;
        }

        _io.SetInput(InputIo.PickupFeederBoltDetected, false);
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        _io.SetInput(InputIo.PickupFeederBoltDetected, true);
    }

    private async Task FeedLinearBoltAsync(int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_linearFeederVersion == version
            && _io.GetOutput(OutputIo.LinearFeederRunSignal))
        {
            _io.SetInput(InputIo.LinearFeederBoltDetected, true);
        }
    }

    private async Task ApplyShootAsync(bool value, int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_shootVersion != version)
        {
            return;
        }

        if (!value)
        {
            _io.SetInput(InputIo.ShootingTubeBoltDetected, false);
            return;
        }

        if (!_io.GetInput(InputIo.ShootingEscapeForward)
            || !_io.GetOutput(OutputIo.BoltHead2VacuumPump))
        {
            return;
        }

        _io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_shootVersion == version
            && _io.GetOutput(OutputIo.ShootBolt))
        {
            _io.SetInput(InputIo.BoltHead2VacuumDetected, true);
        }
    }

    private async Task ReleaseShootingVacuumAsync(int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_shootingVacuumVersion == version)
        {
            _io.SetInput(InputIo.BoltHead2VacuumDetected, false);
        }
    }

    private async Task TransferSupplyCarrierAsync(int version)
    {
        if (_io.GetInput(InputIo.PcbSupplyAvailableFromFront1))
        {
            await Task.Delay(TransferDelayMilliseconds)
                .ConfigureAwait(false);
            if (!SupplyReady(version))
            {
                return;
            }

            _io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
        }

        await Task.Delay(TransferDelayMilliseconds)
            .ConfigureAwait(false);
        if (!SupplyReady(version))
        {
            return;
        }

        _supplyPcbs[0] = true;
        _supplyPcbs[1] = true;
        _io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
    }

    private async Task TransferMainCarrierAsync(int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_mainConveyorTransferVersion != version
            || !_io.GetOutput(OutputIo.MainConveyorRun))
        {
            return;
        }

        if (_io.GetInput(InputIo.InspectionCarrierPresent)
            && _io.GetInput(InputIo.InspectionBackupPlateDown)
            && _io.GetInput(InputIo.InspectionStopperDown)
            && _io.GetOutput(OutputIo.MainConveyorAvailableToRear)
            && _io.GetInput(InputIo.MainConveyorReadyFromRear))
        {
            ClearCarrier(
                InputIo.InspectionCarrierPresent,
                InputIo.InspectionHeatSink1Present,
                InputIo.InspectionHeatSink2Present);
            _io.SetInput(
                InputIo.MainConveyorExitCarrierDetected,
                true);
            await Task.Delay(TransferDelayMilliseconds)
                .ConfigureAwait(false);
            if (_io.GetOutput(OutputIo.MainConveyorRun))
            {
                _io.SetInput(
                    InputIo.MainConveyorExitCarrierDetected,
                    false);
            }
            return;
        }

        if (_io.GetInput(InputIo.BoltFasteningCarrierPresent)
            && _io.GetInput(InputIo.BoltFasteningBackupPlateDown)
            && _io.GetInput(InputIo.InspectionBackupPlateDown)
            && _io.GetInput(InputIo.BoltFasteningStopperDown)
            && _io.GetInput(InputIo.InspectionStopperUp)
            && !_io.GetInput(InputIo.InspectionCarrierPresent))
        {
            MoveCarrier(
                InputIo.BoltFasteningCarrierPresent,
                InputIo.BoltFasteningHeatSink1Present,
                InputIo.BoltFasteningHeatSink2Present,
                InputIo.InspectionCarrierPresent,
                InputIo.InspectionHeatSink1Present,
                InputIo.InspectionHeatSink2Present);
            return;
        }

        if (_io.GetInput(InputIo.PcbPlacementCarrierPresent)
            && _io.GetInput(InputIo.PcbPlacementBackupPlateDown)
            && _io.GetInput(InputIo.BoltFasteningBackupPlateDown)
            && _io.GetInput(InputIo.PcbPlacementStopperDown)
            && _io.GetInput(InputIo.BoltFasteningStopperUp)
            && !_io.GetInput(InputIo.BoltFasteningCarrierPresent))
        {
            MoveCarrier(
                InputIo.PcbPlacementCarrierPresent,
                InputIo.PcbPlacementHeatSink1Present,
                InputIo.PcbPlacementHeatSink2Present,
                InputIo.BoltFasteningCarrierPresent,
                InputIo.BoltFasteningHeatSink1Present,
                InputIo.BoltFasteningHeatSink2Present);
            return;
        }

        if (_io.GetOutput(OutputIo.MainConveyorReadyToFront2)
            && _io.GetInput(InputIo.MainConveyorAvailableFromFront2)
            && _io.GetInput(InputIo.PcbPlacementBackupPlateDown)
            && _io.GetInput(InputIo.PcbPlacementStopperUp)
            && !_io.GetInput(InputIo.PcbPlacementCarrierPresent))
        {
            _io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            _io.SetInput(
                InputIo.MainConveyorEntryCarrierDetected,
                true);
            await Task.Delay(TransferDelayMilliseconds)
                .ConfigureAwait(false);
            if (!_io.GetOutput(OutputIo.MainConveyorRun))
            {
                return;
            }

            _io.SetInput(
                InputIo.MainConveyorEntryCarrierDetected,
                false);
            _io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            _io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            _io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        }
    }

    private async Task PresentMainCarrierAsync(int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_mainConveyorTransferVersion == version
            && _io.GetOutput(OutputIo.MainConveyorReadyToFront2)
            && !_io.GetOutput(OutputIo.MainConveyorRun)
            && !_io.GetInput(InputIo.PcbPlacementCarrierPresent))
        {
            _io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        }
    }

    private void MoveCarrier(
        InputIo sourceCarrier,
        InputIo sourceHeatSink1,
        InputIo sourceHeatSink2,
        InputIo destinationCarrier,
        InputIo destinationHeatSink1,
        InputIo destinationHeatSink2)
    {
        var heatSink1 = _io.GetInput(sourceHeatSink1);
        var heatSink2 = _io.GetInput(sourceHeatSink2);
        ClearCarrier(sourceCarrier, sourceHeatSink1, sourceHeatSink2);
        _io.SetInput(destinationHeatSink1, heatSink1);
        _io.SetInput(destinationHeatSink2, heatSink2);
        _io.SetInput(destinationCarrier, true);
    }

    private void ClearCarrier(
        InputIo carrier,
        InputIo heatSink1,
        InputIo heatSink2)
    {
        _io.SetInput(carrier, false);
        _io.SetInput(heatSink1, false);
        _io.SetInput(heatSink2, false);
    }

    private void ApplyPhysicalOutput(OutputIo output, bool value)
    {
        switch (output)
        {
            case OutputIo.PcbSupplyIpmFixerForward:
                if (value)
                {
                    if (_io.GetInput(InputIo.PcbSupplyPcbDetected))
                    {
                        _supplyHoldingPcb = true;
                        if (_supplyPickupSlot is { } slot)
                        {
                            _supplyPcbs[slot] = false;
                        }
                    }
                }
                else if (_supplyHoldingPcb)
                {
                    _supplyHoldingPcb = false;
                }
                break;
            case OutputIo.PcbPlacementIpmGripperClose:
                if (value
                    && _io.GetInput(InputIo.PcbPlacementPcbDetected)
                    && _io.GetInput(InputIo.PcbPlacementVacuumDetected))
                {
                    _placementHoldingPcb = true;
                }
                else if (!value && _placementHoldingPcb)
                {
                    if (_placementAtBuffer)
                    {
                        _io.SetInput(InputIo.PcbBufferPcbPresent, true);
                    }

                    _placementHoldingPcb = false;
                    _io.SetInput(
                        InputIo.PcbPlacementPcbDetected,
                        _placementAtBuffer
                        || _io.GetInput(
                            InputIo.PcbPlacementHandlerDown));
                }
                break;

            case OutputIo.PcbPlacementHandlerDown when !value:
                _io.SetInput(
                    InputIo.PcbPlacementPcbDetected,
                    _placementHoldingPcb
                    || _placementAtBuffer
                    && _io.GetInput(InputIo.PcbBufferPcbPresent));
                break;

            case OutputIo.ShootingEscapeForward when value:
                _io.SetInput(InputIo.LinearFeederBoltDetected, false);
                break;
            case OutputIo.NgCarrierGripperClose:
                if (value
                    && _inspectionAtNgPickup
                    && _io.GetInput(InputIo.NgCarrierPickupDown)
                    && _io.GetInput(InputIo.InspectionBackupPlateUp)
                    && _io.GetInput(InputIo.InspectionCarrierPresent))
                {
                    _ngCarrierHeld = true;
                    _io.SetInput(InputIo.NgCarrierDetected, true);
                    _io.SetInput(InputIo.InspectionCarrierPresent, false);
                    _io.SetInput(InputIo.InspectionHeatSink1Present, false);
                    _io.SetInput(InputIo.InspectionHeatSink2Present, false);
                }
                else if (!value
                         && _ngCarrierHeld
                         && _inspectionAtNgShuttle
                         && _io.GetInput(InputIo.NgCarrierPickupDown)
                         && _io.GetInput(InputIo.NgShuttleUp))
                {
                    _ngCarrierHeld = false;
                    _io.SetInput(InputIo.NgCarrierDetected, false);
                    _io.SetInput(InputIo.NgShuttleCarrierDetected, true);
                }
                break;
            case OutputIo.NgShuttleDown:
                if (value
                    && _io.GetInput(InputIo.NgShuttleCarrierDetected))
                {
                    _io.SetInput(
                        InputIo.NgConveyorPosition3Occupied,
                        true);
                }
                else if (!value)
                {
                    _io.SetInput(
                        InputIo.NgShuttleCarrierDetected,
                        false);
                }
                break;
        }
    }

    private async Task TransferNgCarrierAsync(int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_ngConveyorVersion != version
            || !_io.GetOutput(OutputIo.NgConveyorRun))
        {
            return;
        }

        var position1 =
            _io.GetInput(InputIo.NgConveyorPosition1Occupied);
        var position2 =
            _io.GetInput(InputIo.NgConveyorPosition2Occupied);
        var position3 =
            _io.GetInput(InputIo.NgConveyorPosition3Occupied);

        if (_io.GetInput(InputIo.NgConveyorStopperDown))
        {
            if (position1)
            {
                _io.SetInput(
                    InputIo.NgConveyorPosition1Occupied,
                    false);
            }

            return;
        }

        if (!position1 && position2)
        {
            _io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
            _io.SetInput(
                InputIo.NgConveyorPosition2Occupied,
                position3);
            _io.SetInput(InputIo.NgConveyorPosition3Occupied, false);
            if (position3)
            {
                _io.SetInput(InputIo.NgShuttleCarrierDetected, false);
            }
            return;
        }

        if (!position1 && position3)
        {
            _io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
            _io.SetInput(InputIo.NgConveyorPosition3Occupied, false);
            _io.SetInput(InputIo.NgShuttleCarrierDetected, false);
            return;
        }

        if (position1 && !position2 && position3)
        {
            _io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
            _io.SetInput(InputIo.NgConveyorPosition3Occupied, false);
            _io.SetInput(InputIo.NgShuttleCarrierDetected, false);
        }
    }

    private bool SupplyReady(int version) =>
        _supplySmemaVersion == version
        && _io.GetOutput(OutputIo.PcbSupplyReadyToFront1);

    private static bool IsAt(
        double x,
        double y,
        AxisPos position) =>
        Math.Abs(x - position.X) <= 0.05
        && Math.Abs(y - position.Y) <= 0.05;

    private static bool IsAt(
        double x,
        double y,
        double z,
        AxisPos position) =>
        IsAt(x, y, z, position.X, position.Y, position.Z);

    private static bool IsAt(
        double x,
        double y,
        double z,
        double targetX,
        double targetY,
        double targetZ) =>
        Math.Abs(x - targetX) <= 0.05
        && Math.Abs(y - targetY) <= 0.05
        && Math.Abs(z - targetZ) <= 0.05;
}
