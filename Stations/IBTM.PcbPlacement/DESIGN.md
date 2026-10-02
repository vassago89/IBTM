# PCB Placement Handler

Run standby uses `HandoffPosition.Z`, `HandoffPosition.X` and the heat-sink Y,
outside Supply's handoff area. START raises IPM and handler, reaches standby Z,
moves Y to the first present heat sink (Heat Sink 1 without a target), then X to
handoff X. After placement, keep that heat sink's Y and return Z followed by X.
This Z is also the XY travel height. `HandoffPosition.Y` is used only for receiving
or returning a PCB. `ReceiveZ` is taught separately at the same handoff X/Y.
Handler Rotate output stays OFF during automatic, repeat and manual operation.
HOME ALL homes Placement in Z -> Y -> X order to avoid interference, then starts
the other units. Each axis must finish successfully before the next starts.
Individual Placement HOME still homes Z first, followed by X/Y together.

1. Wait outside the handoff until Supply reports `Holding`: settled at its give XYZ, Unrotated, and securing the PCB. Then raise the handler, reach standby Z, prepare the IPM and move to standby X followed by Y. Loss of Supply readiness during this approach stops movement.
2. After the awaited X/Y moves, lower Z to `ReceiveZ` in the same approach operation. Keep the handler cylinder Up, detect the PCB and confirm vacuum holding. STOP cancels the operation; a new START does not resume its descent.
3. After Supply fixer and gripper retract, return Z to standby and move only Y to the selected heat sink's placement Y. Keep `PreparingPlacement` until Y settles; only then publish `Clear` for Supply withdrawal. A prefetched PCB without a carrier target waits at Heat Sink 1 Y.
4. With the carrier seated, finish the move to the selected heat sink X, descend to placement Z and lower the handler.
5. Release vacuum, raise IPM and lower IPM to press using the existing IPM Down output. Record the placement, then raise IPM and handler and return Z followed by X to standby, keeping the placement Y. There is no IPM gripper output or open/closed feedback.
6. Wait for Supply readiness before returning to receiving XY for the second PCB, then repeat at Heat Sink 2. Only detected heat sinks are targets; Heat Sink 2 requires no intermediate visit to Heat Sink 1.
7. Complete the carrier only after the final return reaches standby Z and handoff X. Wait there at the last heat sink Y for the next Supply PCB. A cancelled or failed return does not complete the carrier.

`Phase` selects steps within the current run; START selects a fresh initial phase. `AutoUnit.Step` reports only
the current execution/wait and becomes null after STOP. Both are updated through
`EnterStep`; `GetNextStep` and `Handoff` only read state and feedback.
Coordinates do not select stages or heat sinks. `MovingToHandoff` waits for Supply's
`Holding` before approach; `ReturningToSupply` waits for `Released` for a Repeat return.
`PrepareHandoffAsync` awaits standby Z, IPM preparation, X, Y and receive Z in sequence,
then PCB detection and vacuum for normal receipt. One feedback monitor and cancellation
scope covers the operation: Supply must stay ready through descent, then until Placement
confirms its own grip. A Repeat return publishes
`WaitingForSupplyGrip` only after the receive Z move completes. There is no separate descent stage.
`ExecuteStepAsync` returns Z to standby followed by placement Y once Supply is `Released`. Placement publishes `Holding` while securing the PCB at the receiving
position, and `Clear` after the Y departure settles. Internal placement/press stages
are not part of the shared interface. The [handoff contract](../IBTM.PcbSupply/DESIGN.md#direct-handoff-and-live-feedback)
documents these conditions and reference direction.
Handoff readiness uses the completed sequence stage and current axis, lift and grip feedback;
there is no stored handoff coordinate or permanent invalidation after an axis fault.
After Z/Y departure, `WaitingForSupplyDeparture` publishes `Clear` until Supply acknowledges it.
Manual moves do not select a new process stage or start recovery moves.

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

Placement and optional pressing run in `PlacingPcb`. Once the assembly is recorded,
`Retracting` raises IPM and handler and returns Z followed by X before starting
the next PCB or completing the carrier. START uses the same standby movement;
an already completed carrier keeps its completion and is not placed again. A new START does not resume the interrupted rise or placement.
The placement feedback check requires PCB presence through completion; the sensor
may clear during retraction. The operation uses its selected carrier and heat-sink target;
it never guesses a heat sink from X/Y. Repeat uses the same switch in `PcbPlacer.cs` and
picks PCBs from the existing carrier.
With Supply disabled, it visits handoff and places each PCB back on its heat sink.
With Supply enabled, `ReturningPcb` requests the original heat sink's return before
Placement approaches. Placement waits until Supply reports `Released` at its settled,
Unrotated handoff XYZ, then approaches in Z, X, Y order with the same readiness monitoring.
Supply must remain `Released` before and throughout the final descent to Receive Z.
Only after descent completes does Placement publish `Returning`, allowing Supply to grip.
`Returning` confirms a held PCB at Receive Z. Supply secures it before Placement releases and
rises and moves to the original heat sink Y before allowing Supply to withdraw. Placement waits for Supply's departure and next forward handoff, then places
the same PCB without pressing. Supply then completes normal empty withdrawal to
pickup XY and Travel Z before waiting for another return. Repeat keeps IPM Up during pickup, both handoff directions,
travel and placement. Handoff feedback requires an unambiguous IPM endpoint;
the sequence prepares Up for Repeat and Down for normal receipt.
PCB detection and vacuum still confirm holding. Normal production retains the IPM press.
Forward receipt and Repeat pickup both confirm PCB detection and vacuum after
vacuum completes. Only then may Supply release, or Repeat enter `ReturningToSupply`.
Return release requires ready, stopped Placement axes and Supply `Holding` before releasing vacuum.
A missing PCB signal with vacuum ON stops at pickup instead of raising the handler
and trying the pickup again.
Supply returns each PCB to its corresponding pickup XYZ, releases it there and
picks it up again before the forward handoff. Repeat does not use upstream SMEMA
to infer support presence. Placement retains the original carrier and heat-sink
target throughout this round trip.
Main Conveyor OFF repeats the completed seated carrier
with a new work record; it does not clear incomplete work.

`MovingToHandoff`, `ReceivingPcb`, `PreparingPlacement`, `PlacingPcb` and
`Retracting` execute their full actuator/motion sequence before the next
state selection. `PickingPcb` is the repeat pickup operation. Forward receipt waits
for Supply arrival, release, acknowledgement of departure and a seated carrier. `ExecuteStepAsync` returns
false for those waits; it does not split axis moves or vacuum/IPM actions into
separate state-machine ticks. Placement keeps the original job and cancels its
remaining commands if carrier identity or seating changes.
The Repeat argument controls IPM commands throughout the step; there is no second
mode field. After STOP, remove PCBs from the handler and clear vacuum before START.
Machine START also rejects an unfinished carrier still present at S1/S2/S3.
Carrier or support feedback changes during active work stop the run, including waits
between PCBs and the final standby return. Sensor edges never reset the target index
or clear its job. A new admitted carrier starts at the
first present heat sink; recorded results do not select a resume point. A completed carrier stays complete. The original destination
for a Repeat round trip is retained only while that run is active.
