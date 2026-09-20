# PCB Placement Handler

Standby is `HandoffPosition.Z` followed by `HandoffPosition.X/Y`. This Z is
also the XY travel height. `ReceiveZ` is taught separately at the same X/Y.
Handler Rotate output stays OFF during automatic, repeat and manual operation.

1. Raise the handler, reach standby Z, prepare the IPM, then move to standby XY.
2. Wait for Supply at its give XYZ with confirmed holding. Keep the handler cylinder Up, move Z to `ReceiveZ`, detect the PCB, apply vacuum and close the IPM gripper.
3. After Supply fixer and gripper retract, return Z to standby. Both handlers may then leave independently.
4. With the carrier seated, move directly to Heat Sink 1 XY, descend to placement Z and lower the handler.
5. Release vacuum, open the IPM gripper, raise IPM, close the gripper and lower IPM to press. Record the placement, then raise IPM, handler and Z.
6. Return to receiving XY for the second PCB and repeat at Heat Sink 2. Only detected heat sinks are targets; Heat Sink 2 requires no intermediate visit to Heat Sink 1.
7. Complete the carrier after the final placement is raised, then return to receiving standby.

`State` / `GetState` describe Placement feedback and its current repeat operation. `PlaceAsync` starts
receipt when Supply's `Handoff` is `Holding`, and returns Z to standby once it is
`Released`. Placement publishes `Holding` while securing the PCB at the receiving
position, and `Clear` once Supply may withdraw. Internal placement/press stages
are not part of the shared interface. The [handoff contract](../IBTM.PcbSupply/DESIGN.md#direct-handoff-and-live-feedback)
documents these conditions and reference direction.

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

The press target belongs only to the current run because the Down inputs before
and after pressing are identical. STOP discards this history. `PcbPlacer.Repeat.cs`
uses the existing `PcbPlacementState` values and picks PCBs from the existing carrier.
With Supply disabled, it visits handoff and places each PCB back on its heat sink.
With Supply enabled, `ReturningPcb` identifies the original heat sink and `Returning`
confirms a held PCB at Receive Z. Supply secures it before Placement releases and
rises. Placement waits for Supply's departure and next forward handoff, then places
and presses the same PCB. Supply keeps the PCB secured through its reverse travel
at pickup travel height; it does not put the PCB into an upstream slot.
After STOP, live holding feedback selects forward continuation: a PCB on Supply
waits for forward receipt, a PCB held by Placement continues to its heat sink,
and a shared hold at Receive Z waits for Supply release before Placement rises.
Main Conveyor OFF repeats the completed seated carrier
with a new work record; it does not clear incomplete work.

`MovingToHandoff`, `ReceivingPcb`, `PreparingPlacement`, `PlacingPcb` and
`CompletingCarrier` execute their full actuator/motion sequence before the next
state selection. `PickingPcb` is the repeat pickup operation. The three external
waits are Supply arrival, Supply release and a seated carrier. `PlaceAsync` returns
false for those waits; it does not split axis moves or vacuum/gripper actions into
separate state-machine ticks. Placement keeps the original job and cancels its
remaining commands if carrier identity or seating changes.
