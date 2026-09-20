# PCB Supply Handler

This is the behavior contract for `PcbSupplyHandler` and `PcbSupplier`.
The confirmed sequence uses separate rotation and handoff heights.

## Responsibility

- Pick PCB 1 and PCB 2 from the same upstream carrier.
- Secure each PCB with the supply gripper and IPM fixer.
- Pick while Rotated, then unrotate at Rotation Z and approach Placement at the taught handoff Z.
- Hold the PCB until Placement confirms its receiving position and holding inputs.
- Complete the upstream SMEMA handshake after both pickup positions are checked.

## Coordinates

| Teaching item | Use |
| --- | --- |
| PCB Rotation Z | Standby, pickup XY travel, and rotation in either direction |
| PCB 1 Pickup XYZ | First pickup position |
| PCB 2 Pickup XYZ | Second pickup position |
| PCB Handoff XYZ | Handoff Z followed by handoff XY; also the return travel height |

Standby uses PCB 1 Pickup X/Y and PCB Rotation Z, with Rotated feedback confirmed.
There is no separate standby, Clear Z, return coordinate, or collision boundary.
Each pickup XYZ belongs to the recipe; rotation Z and handoff XYZ are machine settings.
A pickup without a taught Y cannot be moved to; teach XYZ and save the recipe.

Only `Record Position` changes `PCB Handoff`. Teaching reads the owning settings
directly, and `Save` persists those coordinates without applying a separate copy.
It now stores Z independently of Rotation Z. Older settings that stored only X/Y
must have handoff Z taught before equipment operation; do not infer it from Rotation Z.

## Normal flow

```text
PCB 1 XY + Rotation Z, rotated
  -> wait for upstream Board Available
  -> selected PCB pickup Z
  -> PCB detection
  -> close supply gripper and confirm
  -> advance IPM fixer and confirm
  -> Rotation Z
  -> rotation IO OFF and unrotated input confirmed
  -> handoff Z
  -> handoff X/Y together, keeping handoff Z
  -> wait for Placement to secure the PCB at its receiving XYZ
  -> retract IPM fixer
  -> open supply gripper
  -> wait for Placement Handler Up, standby Z and settled placement Y
  -> next pickup X/Y together, keeping handoff Z
  -> Rotation Z
  -> rotation IO ON and rotated input confirmed
```

PCB 2 follows the same pickup and handoff sequence, without another upstream
handshake. After PCB 2, the next pickup X/Y is PCB 1 of the next carrier.
If the selected pickup has no PCB, return to Rotation Z without gripping or
rotating and advance to the next slot. After the second empty check, finish the
same carrier handshake.

`MovingToPickup` prepares the PCB 1 standby position even when SMEMA is absent.
`PickingPcb` moves to the selected pickup XY at Rotation Z before descending.
`SetRotatedAsync` always reaches Rotation Z before commanding rotation IO.
`MovingToHandoff` reaches handoff Z before any XY approach. The motion call receives
this height explicitly, so it cannot first move back to the default Rotation Z.
`MovingToPickup` also owns withdrawal: give-height XY to the next pickup, then
Rotation Z and Rotated feedback. `MovingToHandoff` owns PCB securing, rotation and
handoff approach. An already secured, unrotated PCB keeps the give travel height
when resuming an interrupted approach; it does not first return to Rotation Z.

## Direct handoff and live feedback

Each unit owns its sequence enum and computes its state from its own feedback.
Only `Handoff` and `Changed` cross the boundary through Core's
`IPcbSupplyHandoff` / `IPcbPlacementHandoff`. These interfaces expose no handler,
motion commands, or internal sequence stages, and do not store duplicate state.

| Handoff | Confirmed locally | Peer action |
| --- | --- | --- |
| Supply `Holding` | Secured PCB at settled give XYZ with confirmed Unrotated feedback | Placement moves Z to its receive height with its cylinder Up |
| Placement `Holding` | At receive XY/Z, handler Up, confirmed IPM endpoint (Up during active Repeat), PCB detected and vacuum confirmed | Supply retracts fixer, then opens gripper |
| Supply `Released` | At give XYZ with confirmed Unrotated feedback, fixer and gripper released | Placement returns Z to standby, then departs along Y |
| Placement `Clear` | Placement has settled at the heat sink Y after receipt, or is already working at the heat sink / empty at receiving standby | Supply returns to pickup |
| Either unit `Unavailable` | Disabled or not at a confirmed handoff condition | Peer waits |

`MachineController` passes Placement's handoff interface into Supply's run.
Placement receives Supply's handoff interface through DI. Neither project refers
to the other. Each loop listens for the peer's own changes to wake its wait;
it never relays those changes back to the peer.
Supply and Placement approach independently. Every Placement axis movement
requires its handler cylinder Up. Either may arrive first. Both handlers must settle at their
own taught standby/give XYZ before Placement moves Z to `ReceiveZ`.

Supply releases only while Placement reports handoff `Holding`, which
requires PCB detection and vacuum detection at the receive position with the handler Up.
Normal receipt prepares IPM Down and Repeat prepares IPM Up. A stopped, already
secured PCB remains `Holding` with either confirmed IPM endpoint; a contradictory
or unknown endpoint is unavailable. An interrupted forward release resumes the
same recipient checks before each actuator instead of preparing reverse receipt.
After release, Placement returns Z
to standby with its handler cylinder still Up, then moves Y to the selected heat sink while retaining handoff X.
Supply waits for Placement's `Clear` after that Y move before its XY return.
The existing `WaitingForPlacementZ` state also owns this Y departure wait.
`HandingOff` owns both the wait for Placement holding and the fixer/gripper release;
there is no separate release state. Interrupted release still requires confirmed recipient holding.

The give position requires settled X/Y/Z feedback at the stored give XYZ;
matching X/Y at Rotation Z is insufficient when the two heights differ.
Only confirmed recipient holding permits Supply release. Shared-area overlap,
entry/exit checks and collision boundaries are not used.

Rotation and gripper positions come from current paired inputs. Both endpoint
inputs ON or both OFF mean Between. PCB detection alone does not prove holding:
`Secured` also requires closed-gripper and IPM-fixer feedback. Output commands
never substitute for endpoint confirmation.

Both normal and Repeat handoff reject Rotated or Between feedback. Normal operation
stops with an interlock error if this is detected at give XYZ. Release rechecks
Unrotated feedback before opening the gripper, and withdrawal requires it too.
Repeat reverse preparation still reaches Rotation Z and confirms Unrotated before
returning to give XYZ when only the coordinates already match. If Placement is
already holding or returning the PCB at receive Z, invalid rotation stops the
operation without automatically rotating Supply.

Manual axis moves and jog retain the current Z. A handoff point
move requires unrotated feedback. A pickup point move requires rotated feedback.

## SMEMA and slot progress

The local `PickStep` tracks PCB 1, PCB 2, and WaitingForCarrierExit for the current
run and upstream carrier. Live holding and handoff feedback take priority.
Ready stays ON through both pickup checks and the last pickup lift to Rotation Z.
It then falls to tell the upstream equipment that pickup is complete; Placement
need not have received the last PCB yet.

Board Available OFF resets the next slot to PCB 1. A stale ON cannot start another
carrier. If availability disappears during a pickup, cancel that pickup; a late
completion cannot advance a replacement carrier. STOP discards the run's slot
progress without requiring the equipment to be empty. In automatic mode, STOP
preserves Ready while the upstream carrier remains available.

In teaching/manual mode, FRONT 1 `TEST Available` replaces the real SMEMA input.
The selector contact is ON in teaching/manual mode. Mode changes clear TEST.
Automatic SMEMA output calls do not write in teaching; direct manual output
control remains available. TEST OFF followed by ON starts the next carrier.

## Repeat and verification

`PcbSupplier.Repeat.cs` uses the existing `PcbSupplyState` values for the reverse
operations. With Placement disabled, Supply initially picks one PCB, visits
handoff, then returns to pickup XY at Rotation Z while retaining its grip and
fixer. It repeats with that PCB without descending into or releasing at a source
slot. Upstream availability is required only through the initial pickup lift.

With Placement enabled, Placement returns the PCB from its heat sink at Receive Z.
Supply confirms its own grip and fixer before Placement releases and rises. Supply
then travels to pickup XY at Rotation Z with the PCB still secured and performs
the normal forward handoff. This route does not require an upstream support or
Available TEST; Repeat does not feed replacement PCBs.
Both units retain their holding and seating checks in either direction.

STOP discards the current direction. START uses live PCB and position feedback:
Supply holding resumes forward handoff; Placement holding resumes forward
placement. When both hold at handoff, Supply releases only after Placement
confirms holding, and waits for Placement to rise before moving away. Source
slot identity is not needed for restart because no PCB is placed there.
See the [Repeat instructions](../../docs/STATION3_COMMISSIONING.md#repeat).

Focused regressions cover standby before SMEMA, both pickup slots, rotation at
Rotation Z, travel at a different handoff Z, interrupted entry at that same height,
recipient holding feedback, independent departure, and recorded XYZ teaching.
