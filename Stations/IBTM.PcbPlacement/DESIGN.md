# PCB Placement Handler

Standby is `HandoffPosition.Z` followed by `HandoffPosition.X/Y`. This Z is
also the XY travel height. `ReceiveZ` is taught separately at the same X/Y.
Handler Rotate output stays OFF during automatic, repeat and manual operation.

1. Raise the handler, reach standby Z, prepare the IPM, then move to standby XY.
2. Wait for Supply at its give XYZ with confirmed holding. Keep the handler cylinder Up, move Z to `ReceiveZ`, detect the PCB and confirm vacuum holding.
3. After Supply fixer and gripper retract, return Z to standby and move only Y to the selected heat sink's placement Y. Keep `PreparingPlacement` until Y settles; only then publish `Clear` for Supply withdrawal. A prefetched PCB without a carrier target waits at Heat Sink 1 Y.
4. With the carrier seated, finish the move to the selected heat sink X, descend to placement Z and lower the handler.
5. Release vacuum, raise IPM and lower IPM to press using the existing IPM Down output. Record the placement, then raise IPM, handler and Z. There is no IPM gripper output or open/closed feedback.
6. Return to receiving XY for the second PCB and repeat at Heat Sink 2. Only detected heat sinks are targets; Heat Sink 2 requires no intermediate visit to Heat Sink 1.
7. Complete the carrier after the final placement is raised, then return to receiving standby.

`State` / `GetState` describe Placement feedback and its current repeat operation. `PlaceAsync` starts
receipt when Supply's `Handoff` is `Holding`, and returns Z to standby followed by placement Y once it is
`Released`. Placement publishes `Holding` while securing the PCB at the receiving
position, and `Clear` after the Y departure settles. Internal placement/press stages
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
rises and moves to the original heat sink Y before allowing Supply to withdraw. Placement waits for Supply's departure and next forward handoff, then places
the same PCB without pressing. Repeat keeps IPM Up during pickup, both handoff directions,
travel and placement; its handoff confirmation requires IPM Up instead of Down.
PCB detection and vacuum still confirm holding. Normal production retains the IPM press.
Supply keeps the PCB secured through its reverse travel
at pickup travel height; it does not put the PCB into an upstream slot.
After STOP, live holding feedback selects forward continuation: a PCB on Supply
waits for forward receipt, a PCB held by Placement continues to its heat sink,
and a shared hold at Receive Z waits for Supply release before Placement rises and departs in Y.
After STOP, a PCB confirmed by receive XYZ, handler Up and vacuum remains `Holding`
with either confirmed IPM endpoint. An unknown or contradictory IPM position is unavailable.
Normal restart prepares IPM Down after Supply releases; Repeat keeps IPM Up.
A stop partway through departure resumes Y before moving X; no additional sequence state is used.
Main Conveyor OFF repeats the completed seated carrier
with a new work record; it does not clear incomplete work.

`MovingToHandoff`, `ReceivingPcb`, `PreparingPlacement`, `PlacingPcb` and
`CompletingCarrier` execute their full actuator/motion sequence before the next
state selection. `PickingPcb` is the repeat pickup operation. The three external
waits are Supply arrival, Supply release and a seated carrier. `PlaceAsync` returns
false for those waits; it does not split axis moves or vacuum/IPM actions into
separate state-machine ticks. Placement keeps the original job and cancels its
remaining commands if carrier identity or seating changes.
