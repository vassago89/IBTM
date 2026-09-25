# PCB Placement Handler

Standby is `HandoffPosition.Z` followed by `HandoffPosition.X/Y`. This Z is
also the XY travel height. `ReceiveZ` is taught separately at the same X/Y.
Handler Rotate output stays OFF during automatic, repeat and manual operation.

1. Raise the handler, reach standby Z, prepare the IPM, then move to standby X followed by Y. Automatic and Repeat use this same approach regardless of Supply enablement.
2. Wait for Supply at its give XYZ with confirmed holding. Keep the handler cylinder Up, move Z to `ReceiveZ`, detect the PCB and confirm vacuum holding.
3. After Supply fixer and gripper retract, return Z to standby and move only Y to the selected heat sink's placement Y. Keep `PreparingPlacement` until Y settles; only then publish `Clear` for Supply withdrawal. A prefetched PCB without a carrier target waits at Heat Sink 1 Y.
4. With the carrier seated, finish the move to the selected heat sink X, descend to placement Z and lower the handler.
5. Release vacuum, raise IPM and lower IPM to press using the existing IPM Down output. Record the placement, then raise IPM, handler and Z. There is no IPM gripper output or open/closed feedback.
6. Return to receiving XY for the second PCB and repeat at Heat Sink 2. Only detected heat sinks are targets; Heat Sink 2 requires no intermediate visit to Heat Sink 1.
7. Complete the carrier after the final placement is raised, then return to receiving standby.

`Phase` retains unfinished handoff progress in this unit. `AutoUnit.Step` reports only
the current execution/wait and becomes null after STOP. Both are updated through
`EnterStep`; `GetNextStep` and `Handoff` only read state and feedback.
Coordinates do not select stages or heat sinks. `ExecuteStepAsync` starts
receipt when Supply's `Handoff` is `Holding`, and returns Z to standby followed by placement Y once it is
`Released`. Placement publishes `Holding` while securing the PCB at the receiving
position, and `Clear` after the Y departure settles. Internal placement/press stages
are not part of the shared interface. The [handoff contract](../IBTM.PcbSupply/DESIGN.md#direct-handoff-and-live-feedback)
documents these conditions and reference direction.
Starting another axis move clears the previous handoff completion; manual moves do not select a new process stage or start recovery moves.

Teaching lists `PCB Receive Standby` (XYZ, Save) and
`PCB Receive Z` (Z only, saved automatically). Existing settings retain their
standby coordinates. Missing `ReceiveZ` remains untaught and cannot start receipt.

Placement reads `RecipeManager.Current.PcbPlacement` for both state selection and
motion targets; callers do not pass a second recipe into the execution path.

Manual Z Jog/Step can adjust the axis while the handler is lowered. Handler Up is
required before and during X/Y movement, Move To, automatic Z movement and HOME.
The handler cannot be commanded Down while an axis moves. Actual arrival,
seated-carrier and holding/release feedback remain in use. Supply area departure
and relative handler positions do not gate this sequence.

Placement, optional pressing and retraction run in the `PlacingPcb` branch of `ExecuteStepAsync`.
Its local feedback check requires PCB presence through completion, then permits
the sensor to clear during retraction. The operation uses its selected carrier and heat-sink target;
it never guesses a heat sink from X/Y. Repeat uses the same switch in `PcbPlacer.cs` and
picks PCBs from the existing carrier.
With Supply disabled, it visits handoff and places each PCB back on its heat sink.
With Supply enabled, `ReturningPcb` identifies the original heat sink and `Returning`
confirms a held PCB at Receive Z. Supply secures it before Placement releases and
rises and moves to the original heat sink Y before allowing Supply to withdraw. Placement waits for Supply's departure and next forward handoff, then places
the same PCB without pressing. Repeat keeps IPM Up during pickup, both handoff directions,
travel and placement. Handoff feedback requires an unambiguous IPM endpoint;
the sequence prepares Up for Repeat and Down for normal receipt.
PCB detection and vacuum still confirm holding. Normal production retains the IPM press.
Repeat pickup confirms both signals after vacuum completes and only then enters
`ReturningToSupply`. A missing PCB signal with vacuum ON stops at pickup instead of
raising the handler and trying the pickup again.
Supply keeps the PCB secured through its reverse travel
at pickup travel height; it does not put the PCB into an upstream slot.
Main Conveyor OFF repeats the completed seated carrier
with a new work record; it does not clear incomplete work.

`MovingToHandoff`, `ReceivingPcb`, `PreparingPlacement`, `PlacingPcb` and
`CompletingCarrier` execute their full actuator/motion sequence before the next
state selection. `PickingPcb` is the repeat pickup operation. Forward receipt waits
for Supply arrival, release, acknowledgement of departure and a seated carrier. `ExecuteStepAsync` returns
false for those waits; it does not split axis moves or vacuum/IPM actions into
separate state-machine ticks. Placement keeps the original job and cancels its
remaining commands if carrier identity or seating changes.
The Repeat argument controls IPM commands throughout the step; there is no second
mode field. An unfinished Repeat cannot enter a normal run or publish a return
request to Supply during that rejected start.
