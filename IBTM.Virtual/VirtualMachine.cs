using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMachine
{
    private const int TransferDelayMilliseconds = 200;

    private readonly VirtualIoService _io;
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

    public VirtualMachine(VirtualIoService io)
    {
        _io = io;
        io.OutputChanged += OnOutputChanged;
        io.OutputApplied += ApplyPhysicalOutput;
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        io.SetInput(InputIo.PickupFeederBoltDetected, true);
    }

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
                         InputIo.PcbPlacementCarrierJigPresent))
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

        if (_io.GetInput(InputIo.InspectionCarrierJigPresent)
            && _io.GetInput(InputIo.InspectionBackupPlateDown)
            && _io.GetInput(InputIo.InspectionStopperDown)
            && _io.GetOutput(OutputIo.MainConveyorAvailableToRear)
            && _io.GetInput(InputIo.MainConveyorReadyFromRear))
        {
            ClearCarrier(
                InputIo.InspectionCarrierJigPresent,
                InputIo.InspectionHousing1Present,
                InputIo.InspectionHousing2Present);
            return;
        }

        if (_io.GetInput(InputIo.BoltFasteningCarrierJigPresent)
            && _io.GetInput(InputIo.BoltFasteningBackupPlateDown)
            && _io.GetInput(InputIo.InspectionBackupPlateDown)
            && _io.GetInput(InputIo.BoltFasteningStopperDown)
            && _io.GetInput(InputIo.InspectionStopperUp)
            && !_io.GetInput(InputIo.InspectionCarrierJigPresent))
        {
            MoveCarrier(
                InputIo.BoltFasteningCarrierJigPresent,
                InputIo.BoltFasteningHousing1Present,
                InputIo.BoltFasteningHousing2Present,
                InputIo.InspectionCarrierJigPresent,
                InputIo.InspectionHousing1Present,
                InputIo.InspectionHousing2Present);
            return;
        }

        if (_io.GetInput(InputIo.PcbPlacementCarrierJigPresent)
            && _io.GetInput(InputIo.PcbPlacementBackupPlateDown)
            && _io.GetInput(InputIo.BoltFasteningBackupPlateDown)
            && _io.GetInput(InputIo.PcbPlacementStopperDown)
            && _io.GetInput(InputIo.BoltFasteningStopperUp)
            && !_io.GetInput(InputIo.BoltFasteningCarrierJigPresent))
        {
            MoveCarrier(
                InputIo.PcbPlacementCarrierJigPresent,
                InputIo.PcbPlacementHousing1Present,
                InputIo.PcbPlacementHousing2Present,
                InputIo.BoltFasteningCarrierJigPresent,
                InputIo.BoltFasteningHousing1Present,
                InputIo.BoltFasteningHousing2Present);
            return;
        }

        if (_io.GetOutput(OutputIo.MainConveyorReadyToFront2)
            && _io.GetInput(InputIo.MainConveyorAvailableFromFront2)
            && _io.GetInput(InputIo.PcbPlacementBackupPlateDown)
            && _io.GetInput(InputIo.PcbPlacementStopperUp)
            && !_io.GetInput(InputIo.PcbPlacementCarrierJigPresent))
        {
            _io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            _io.SetInput(InputIo.PcbPlacementHousing1Present, true);
            _io.SetInput(InputIo.PcbPlacementHousing2Present, true);
            _io.SetInput(InputIo.PcbPlacementCarrierJigPresent, true);
        }
    }

    private async Task PresentMainCarrierAsync(int version)
    {
        await Task.Delay(TransferDelayMilliseconds).ConfigureAwait(false);
        if (_mainConveyorTransferVersion == version
            && _io.GetOutput(OutputIo.MainConveyorReadyToFront2)
            && !_io.GetOutput(OutputIo.MainConveyorRun)
            && !_io.GetInput(InputIo.PcbPlacementCarrierJigPresent))
        {
            _io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        }
    }

    private void MoveCarrier(
        InputIo sourceCarrier,
        InputIo sourceHousing1,
        InputIo sourceHousing2,
        InputIo destinationCarrier,
        InputIo destinationHousing1,
        InputIo destinationHousing2)
    {
        var housing1 = _io.GetInput(sourceHousing1);
        var housing2 = _io.GetInput(sourceHousing2);
        ClearCarrier(sourceCarrier, sourceHousing1, sourceHousing2);
        _io.SetInput(destinationHousing1, housing1);
        _io.SetInput(destinationHousing2, housing2);
        _io.SetInput(destinationCarrier, true);
    }

    private void ClearCarrier(
        InputIo carrier,
        InputIo housing1,
        InputIo housing2)
    {
        _io.SetInput(carrier, false);
        _io.SetInput(housing1, false);
        _io.SetInput(housing2, false);
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
                        _placementAtBuffer);
                }
                break;

            case OutputIo.ShootingEscapeForward when value:
                _io.SetInput(InputIo.LinearFeederBoltDetected, false);
                break;
            case OutputIo.NgCarrierGripperClose:
                if (value
                    && _inspectionAtNgPickup
                    && _io.GetInput(InputIo.NgCarrierPickupDown)
                    && _io.GetInput(InputIo.InspectionBackupPlateUp)
                    && _io.GetInput(InputIo.InspectionCarrierJigPresent))
                {
                    _ngCarrierHeld = true;
                    _io.SetInput(InputIo.NgCarrierJigDetected, true);
                    _io.SetInput(InputIo.InspectionCarrierJigPresent, false);
                    _io.SetInput(InputIo.InspectionHousing1Present, false);
                    _io.SetInput(InputIo.InspectionHousing2Present, false);
                }
                else if (!value
                         && _ngCarrierHeld
                         && _inspectionAtNgShuttle
                         && _io.GetInput(InputIo.NgCarrierPickupDown)
                         && _io.GetInput(InputIo.NgShuttleUp))
                {
                    _ngCarrierHeld = false;
                    _io.SetInput(InputIo.NgCarrierJigDetected, false);
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
            return;
        }

        if (!position1 && position3)
        {
            _io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
            _io.SetInput(InputIo.NgConveyorPosition3Occupied, false);
            return;
        }

        if (position1 && !position2 && position3)
        {
            _io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
            _io.SetInput(InputIo.NgConveyorPosition3Occupied, false);
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
