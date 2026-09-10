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
            ConveyorState = _state.MainConveyorState,
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
                ? _inspectionStation.State(
                    bolts,
                    _state.RepeatEnabled,
                    holdAtShuttle: _state.RepeatEnabled && !_units.NgShuttle)
                : InspectionStationState.Waiting,
            InspectionBolt = teachingReady && _units.Inspection && automatic
                ? _inspectionStation.ActiveBolt(bolts)
                : null,
            InspectionPcb = teachingReady && _units.Inspection && automatic
                ? _inspectionStation.ActivePcb(bolts)
                : null,
            MainConveyorPathBlock = conveyorPathBlock,
            RepeatPhase = _repeatPhase,
            RepeatCycles = _repeatCycles,
        };
    }
}
