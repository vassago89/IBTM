using System;
using System.Linq;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;

namespace IBTM;

public sealed partial class MachineController
{
    // UI snapshots are display hints; commands must recheck live interlocks.
    private MachineDisplay ReadDisplay()
    {
        // Preserve the initialization/connection fault without reading closed I/O.
        // The unavailable snapshot leaves all movement commands disabled.
        if (!_io.IsReady)
        {
            return new()
            {
                Alarm = _state.Alarm,
                AlarmDetail = _state.AlarmDetail,
                AlarmMessage = _state.AlarmMessage,
                IsRunning = _state.IsRunning,
                IsHoming = _state.IsHoming,
                SetupEditingEnabled = _state.SetupEditingEnabled,
                AutomaticRunning = _state.AutomaticRunning,
                StartBlock = StartBlockReason.Alarm,
                HomeBlock = HomeBlockReason.IoUnavailable,
                ManualBlock = ManualControlBlock.Alarm,
            };
        }

        var motion = _state.DisplayMotionReadiness;
        var block = GetStartBlock(motion);
        var servoPower = _state.ServoMainContactorOn && motion.ServosOn;
        var conflict = _state.BufferConflict;
        var running = _state.IsRunning;
        var safetyReady = _state.SafetyReady;
        var teachingReady = TeachingReady;
        var bolts = _recipe.Pcb.GetBolts().ToArray();
        var automatic = _state.AutomaticRunning;
        var conveyorPathBlock = GetMainConveyorPathBlock();

        return new()
        {
            Available = true,
            SafetyReady = safetyReady,
            MotionFaulted = motion.Faulted,
            IsRunning = running,
            StartBlock = block,
            HomeBlock = HomeBlock,
            IsHoming = _state.IsHoming,
            AutomaticRunning = automatic,
            ConveyorRunning = _state.ConveyorRunning,
            ConveyorState = _state.MainConveyorState,
            NgConveyorRunning = _ngConveyor.RunCommandOn,
            NgConveyorState = _ngConveyor.State,
            BufferConflict = conflict,
            SupplyInBufferArea = _state.SupplyInBufferArea,
            PlacementInBufferArea = _state.PlacementInBufferArea,
            SupplyAtHandoff = _state.SupplyAtHandoff,
            CanSupplyEnter = _state.CanSupplyEnter,
            EmergencyStopReleased = _state.EmergencyStopReleased,
            DoorClosed = _state.DoorClosed,
            AirPressureOk = _state.AirPressureOk,
            AutoMode = _state.AutoMode,
            Alarm = _state.Alarm,
            AlarmDetail = _state.AlarmDetail,
            AlarmMessage = _state.AlarmMessage,
            ServoPowerOn = servoPower,
            Homed = motion.Homed,
            CanStart = IsStartAllowed(block),
            CanHome = IsHomeAllowed(motion),
            CanRaiseCylinders = CanRaiseCylinders,
            HomeableAxes = Enum.GetValues<MotionGroup>()
                .SelectMany(
                    group => _state.GetMotionStatus(group).Feedback.Axes.Select(
                        axis => (
                            group,
                            axis)))
                .Where(item => CanHomeAxis(item.group, item.axis, live: false))
                .ToHashSet(),
            ManualBlock = _state.GetManualBlock(motion),
            ManualSetupEnabled = _state.ManualSetupEnabled,
            SetupEditingEnabled = _state.SetupEditingEnabled,
            PlacementState = _units.PcbPlacement
                ? _pcbPlacement.State(_recipe.PcbPlacement)
                : PcbPlacementState.WaitingForBufferPcb,
            PlacementTarget = _pcbPlacement.TargetHeatSink,
            FasteningState = teachingReady && _units.BoltFastening
                ? _fasteningStation.State()
                : BoltFasteningState.Waiting,
            FasteningBolt = teachingReady && _units.BoltFastening && automatic
                ? _fasteningStation.ActiveBolt()
                : null,
            InspectionState = teachingReady && _units.Inspection
                ? _inspectionStation.State(bolts)
                : InspectionStationState.Waiting,
            InspectionBolt = teachingReady && _units.Inspection && automatic
                ? _inspectionStation.ActiveBolt(bolts)
                : null,
            InspectionPcb = teachingReady && _units.Inspection && automatic
                ? _inspectionStation.ActivePcb(bolts)
                : null,
            NgTransferDryRunState = _units.NgCarrierTransfer && _inspectionGantry.Motion.XyHomed
                ? _ngTransferDryRun.State
                : NgTransferState.Unavailable,
            NgTransferDestination = _ngTransferDryRun.Destination,
            NgTransferDryRunTransfers = _ngTransferDryRun.CompletedTransfers,
            InspectionDryRunReady = _inspectionDryRun.Ready,
            InspectionDryRunState = _units.Inspection && _inspectionGantry.Motion.XyHomed
                ? _inspectionDryRun.State
                : InspectionDryRunState.Unavailable,
            InspectionDryRunDirection = _inspectionDryRun.Direction,
            InspectionDryRunPasses = _inspectionDryRun.CompletedPasses,
            InspectionDryRunPcb = _inspectionDryRun.ActivePcb,
            InspectionDryRunBolt = _inspectionDryRun.ActiveBolt,
            InspectionDryRunBarcode = _inspectionDryRun.LastBarcode,
            InspectionDryRunBoltPresent = _inspectionDryRun.LastBoltPresent,
            MainConveyorPathBlock = conveyorPathBlock,
            MainConveyorDryRunState = conveyorPathBlock == OutputBlockReason.None
                ? _mainConveyorDryRun.State
                : MainConveyorDryRunState.Unavailable,
            MainConveyorDestination = _mainConveyorDryRun.Destination,
            MainConveyorDryRunPasses = _mainConveyorDryRun.CompletedPasses,
            PcbReturnState = PcbReturnNeedsCarrier && _mainConveyorDryRun.ReturningToStation1
                ? _mainConveyorDryRun.State
                : _pcbReturn.State,
            PcbReturnDestination = PcbReturnNeedsCarrier && _mainConveyorDryRun.ReturningToStation1
                ? _mainConveyorDryRun.Destination
                : _pcbReturn.Destination,
            PcbReturnCount = _pcbReturn.CompletedReturns,
            PcbReturnHeatSink = _pcbReturn.HeatSink,
            PcbDryRunState = _pcbDryRun.State,
            PcbDryRunDirection = _pcbDryRun.Direction,
            PcbDryRunHeatSink = _pcbDryRun.HeatSink,
            PcbDryRunCycles = _pcbDryRun.CompletedCycles,
            NgConveyorDryRunState = _ngConveyorDryRun.State,
            NgConveyorDestination = _ngConveyorDryRun.Destination,
            NgConveyorDryRunPasses = _ngConveyorDryRun.CompletedPasses,
            NgConveyorDryRunReady = _ngConveyorDryRun.Ready,
            BoltRouteReady = _boltRoute.Ready,
            BoltRouteState = _units.BoltFastening && _fasteningGantry.Motion.XyHomed
                ? _boltRoute.State
                : BoltRouteState.Unavailable,
            BoltRouteDirection = _boltRoute.Direction,
            BoltRouteTarget = _boltRoute.ActiveBolt,
            BoltRoutePass = _boltRoute.ActivePass,
            BoltRoutePasses = _boltRoute.CompletedPasses,
        };
    }
}
