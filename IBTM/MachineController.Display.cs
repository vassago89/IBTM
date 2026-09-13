using System;
using System.IO;
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
    internal MachineDisplay ReadDisplay()
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

        var mainRunning = _feedback.Io.Outputs[OutputIo.MainConveyorRun].IsOn
            ?? throw new IOException("Main conveyor output feedback is unavailable.");
        var ngRunning = _feedback.Io.Outputs[OutputIo.NgConveyorRun].IsOn
            ?? throw new IOException("NG conveyor output feedback is unavailable.");
        var motion = _state.FeedbackReadiness;
        var servoPower = _state.ServoMainContactorOn && motion.ServosOn;
        var conflict = _state.Buffer.HasConflict(live: false);
        var block = GetStartBlock(motion, conflict);
        var running = _state.GetIsRunning(mainRunning, ngRunning);
        var safetyReady = _state.SafetyReady;
        var teachingReady = TeachingReady;
        var bolts = _recipe.Pcb.GetBolts().ToArray();
        var automatic = _state.AutomaticRunning;
        var setupEditing = !_operations.IsShuttingDown && _state.ManualMode && !running;
        var manualSetup = setupEditing && safetyReady;
        var fasteningState = teachingReady && _units.BoltFastening
            ? _fasteningStation.State(live: false)
            : BoltFasteningState.Waiting;

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
            ConveyorState = _conveyor.ReadState(mainRunning),
            NgConveyorState = _ngConveyor.ReadState(ngRunning),
            BufferConflict = conflict,
            SupplyInBufferArea = _state.Buffer.IsSupplyInside(live: false),
            PlacementInBufferArea = _state.Buffer.IsPlacementInside(live: false),
            SupplyAtHandoff = _state.Buffer.IsSupplyAtHandoff(live: false),
            CanSupplyEnter = _state.Buffer.CanEnterSupply(live: false),
            EmergencyStopReleased = _state.EmergencyStopReleased,
            DoorClosed = _state.DoorClosed,
            AirPressureOk = _state.AirPressureOk,
            AutoMode = _state.AutoMode,
            Alarm = _state.Alarm,
            AlarmDetail = _state.AlarmDetail,
            AlarmMessage = _state.AlarmMessage,
            ServoPowerOn = servoPower,
            Homed = motion.Homed,
            CanStart = IsStartAllowed(block, running),
            CanHome = IsHomeAllowed(motion, running),
            CanRaiseCylinders = manualSetup
                && (BufferHandlersEnabled || _units.BoltFastening || InspectionGantryEnabled)
                && !Array.Exists(CarrierInputs, _io.GetInput),
            HomeableAxes = Enum.GetValues<MotionGroup>()
                .SelectMany(
                    group => _state.GetMotionStatus(group).Feedback.Axes.Select(
                        axis => (
                            group,
                            axis)))
                .Where(item => CanHomeAxis(item.group, item.axis, live: false, running: running))
                .ToHashSet(),
            ManualBlock = _state.GetManualBlock(motion, conflict, running),
            ManualSetupEnabled = manualSetup,
            SetupEditingEnabled = setupEditing,
            PlacementState = _units.PcbPlacement
                ? _pcbPlacement.State(_recipe.PcbPlacement, live: false)
                : PcbPlacementState.WaitingForBufferPcb,
            PlacementTarget = _pcbPlacement.TargetHeatSink,
            FasteningState = fasteningState,
            FasteningBolt = teachingReady && _units.BoltFastening && automatic
                ? _fasteningStation.ActiveBolt(fasteningState)
                : null,
            InspectionState = teachingReady && _units.Inspection
                ? _inspectionStation.State(
                    bolts,
                    _state.RepeatEnabled,
                    holdAtShuttle: _state.RepeatEnabled && !_units.NgShuttle,
                    live: false,
                    conveyorRunning: ngRunning)
                : InspectionStationState.Waiting,
            InspectionBolt = teachingReady && _units.Inspection && automatic
                ? _inspectionStation.ActiveBolt(bolts)
                : null,
            InspectionPcb = teachingReady && _units.Inspection && automatic
                ? _inspectionStation.ActivePcb(bolts)
                : null,
            RepeatPhase = _repeatPhase,
            RepeatCycles = _repeatCycles,
        };
    }
}
