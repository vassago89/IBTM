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
        var block = GetStartBlock(motion);
        var running = _state.IsRunningFor(mainRunning, ngRunning);
        var safetyReady = _state.SafetyReady;
        var teachingReady = TeachingReady;
        var bolts = _recipes.Current.Pcb.BoltPoints.ToArray();
        var automatic = _state.AutomaticRunning;
        var setupEditing = _state.SetupEditingEnabled;
        var fasteningState = teachingReady && _units.BoltFastening
            ? _fasteningStation.GetState(live: false)
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
            ConveyorState = _conveyor.GetState(mainRunning, live: false),
            NgConveyorState = _ngConveyor.GetState(ngRunning),
            SupplyAtHandoff = _state.Buffer.IsSupplyAtHandoff(live: false),
            EmergencyStopReleased = _state.EmergencyStopReleased,
            DoorClosed = _state.DoorClosed,
            AirPressureOk = _state.AirPressureOk,
            AutoMode = _state.AutoMode,
            Alarm = _state.Alarm,
            AlarmDetail = _state.AlarmDetail,
            AlarmMessage = _state.AlarmMessage,
            ServoPowerOn = servoPower,
            Homed = motion.Homed,
            IsStartAllowed = IsStartAllowedFor(block, running),
            IsHomeAllowed = IsHomeAllowedFor(motion, running),
            HomeableAxes = Enum.GetValues<MotionGroup>()
                .SelectMany(
                    group => _state.GetMotionStatus(group).Feedback.Axes.Select(
                        axis => (
                            group,
                            axis)))
                .Where(item => !running && IsHomeAxisReady(item.group, item.axis, live: false))
                .ToHashSet(),
            ManualBlock = _state.GetManualBlock(motion, running),
            SetupEditingEnabled = setupEditing,
            PlacementState = _units.PcbPlacement
                ? _pcbPlacement.GetState(_recipes.Current.PcbPlacement, live: false)
                : PcbPlacementState.WaitingForSupply,
            PlacementTarget = _pcbPlacement.TargetHeatSink,
            FasteningState = fasteningState,
            FasteningBolt = teachingReady && _units.BoltFastening && automatic
                ? _fasteningStation.GetActiveBolt(fasteningState)
                : null,
            InspectionState = teachingReady && _units.Inspection
                ? _inspectionStation.GetState(
                    bolts,
                    _state.RepeatEnabled,
                    holdAtShuttle: _state.RepeatEnabled && !_units.NgShuttle,
                    live: false,
                    conveyorRunning: ngRunning)
                : InspectionStationState.Waiting,
            InspectionBolt = teachingReady && _units.Inspection && automatic
                ? _inspectionStation.GetActiveBolt(bolts)
                : null,
            InspectionPcb = teachingReady && _units.Inspection && automatic
                ? _inspectionStation.GetActivePcb(bolts)
                : null,
            RepeatPhase = RepeatDisplayPhase,
            RepeatCycles = _repeatCycles,
        };
    }
}
