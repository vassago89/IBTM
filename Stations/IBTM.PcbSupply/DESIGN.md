# PCB Supply Handler

This is the behavior contract for `PcbSupplier`, which owns both motion and the supply sequence.
Travel Z is the horizontal travel height. Rotation in either direction is allowed only at settled handoff XYZ.

## Responsibility

- Pick PCB 1 and PCB 2 from the same upstream carrier.
- Secure each PCB with the supply gripper and IPM fixer.
- Pick while Rotated, travel to handoff XYZ, then confirm Unrotated before offering the PCB to Placement.
- Hold the PCB until Placement confirms its receiving position and holding inputs.
- Complete the upstream SMEMA handshake after both pickup positions are checked.

## Coordinates

| Teaching item | Use |
| --- | --- |
| PCB Travel Z | Standby and horizontal travel in either orientation |
| PCB 1 Pickup XYZ | First pickup position |
| PCB 2 Pickup XYZ | Second pickup position |
| PCB Handoff XYZ | The only rotation position, and the handoff position |

Standby uses PCB 1 Pickup X/Y and PCB Travel Z, with Rotated feedback confirmed.
There is no separate standby, Clear Z, return coordinate, or collision boundary.
Pickup XYZ belongs to the recipe; Travel Z and handoff XYZ are machine settings.
`TravelZ` retains the JSON name `RotationZ` so existing saved heights are preserved.
A pickup without a taught Y cannot be moved to; teach XYZ and save the recipe.
Only `Record Position` changes `PCB Handoff`. Its Z is independent of Travel Z.

## Normal flow

```text
PCB 1 XY + Travel Z, rotated
  -> wait for upstream Board Available
  -> selected PCB pickup Z
  -> PCB detection
  -> close supply gripper and confirm
  -> advance IPM fixer and confirm
  -> Travel Z
  -> handoff X/Y together
  -> handoff Z
  -> rotation IO OFF and unrotated input confirmed at handoff XYZ
  -> wait for Placement to secure the PCB at its receiving XYZ
  -> retract IPM fixer
  -> open supply gripper
  -> wait for Placement Handler Up, standby Z and settled placement Y
  -> rotation IO ON and rotated input confirmed at handoff XYZ
  -> Travel Z
  -> next pickup X/Y together
```

PCB 2 follows the same pickup and handoff sequence, without another upstream
handshake. After PCB 2, the next pickup X/Y is PCB 1 of the next carrier.
If a pickup has no PCB, return to Travel Z without gripping or rotating and
advance to the next slot. The second empty check completes the same handshake.

`MovingToPickup` prepares the PCB 1 standby position even when SMEMA is absent.
If rotation is required on startup, move Travel Z -> handoff XY -> handoff Z,
confirm Rotated there, then travel to standby.
`PrepareHandoffAsync` uses Travel Z -> handoff XY -> handoff Z, preserving the
current confirmed orientation during travel. It confirms Unrotated at handoff XYZ
before publishing a completed handoff. `MoveFromHandoffAsync` rotates at handoff
XYZ and moves through Travel Z to the selected pickup XY. Interrupted withdrawal
with confirmed Rotated feedback continues through Travel Z without rotating again.

`SetRotatedAsync` never moves an axis. It requires homed, servo-on, settled XYZ
at the configured handoff position and stops waiting if that condition is lost.
The teaching rotation button and direct OUTPUTS toggle enforce this same position
condition. Move to the taught handoff position first; changing Z alone is insufficient.

## Direct handoff and live feedback

Each unit owns its sequence enum and advances its process stage after the commanded operation completes.
Coordinates never select a process stage or pickup slot.
Program startup and manual motion do not reconstruct progress from matching teaching coordinates.
Sensor feedback still validates each operation and handoff.
Handoff readiness uses the completed sequence stage and current axis, rotation and grip feedback.
It does not store or continuously compare a handoff coordinate snapshot. Axis faults make it unavailable;
restoring feedback does not require repeating the stage. START sends a retained PCB through the normal forward move before publishing handoff readiness again.
Only `Handoff` and `Changed` cross the boundary through Core's
`IPcbSupplyHandoff` / `IPcbPlacementHandoff`. These interfaces expose no handler,
motion commands, or internal sequence stages, and do not store duplicate state.

| Handoff | Confirmed locally | Peer action |
| --- | --- | --- |
| Supply `Holding` | Handoff move completed, axes ready and stopped, Unrotated and PCB grip confirmed | Placement moves Z to its receive height with its cylinder Up |
| Placement `Holding` | Receipt completed, axes ready and stopped, handler Up, confirmed IPM endpoint, PCB detected and vacuum confirmed | Supply retracts fixer, then opens gripper |
| Supply `Released` | Handoff stage, axes ready and stopped, Unrotated, fixer and gripper released | Placement returns Z to standby, then departs along Y |
| Placement `Clear` | Z/Y departure completed, ready axes and raised handler; waiting for Supply departure | Supply returns to pickup |
| Either unit `Unavailable` | Disabled or not at a confirmed handoff condition | Peer waits |

`MachineController` passes Placement's handoff interface into Supply's run.
Placement receives Supply's handoff interface through DI. Neither project refers
to the other. Each loop listens for the peer's own changes to wake its wait;
it never relays those changes back to the peer.
Supply reaches its handoff XYZ and confirms Unrotated before Placement approaches.
Placement waits outside the handoff while Supply is unavailable: forward receipt requires
`Holding`; a Repeat return requires `Released`. Placement monitors this condition through
its Z, X, then Y approach and cancels movement if Supply readiness is lost.
Every Placement axis movement requires its handler cylinder Up. Both handlers must settle at
their own taught standby/give XYZ before Placement moves Z to `ReceiveZ`.

Supply releases only while Placement reports handoff `Holding`, which
requires PCB detection and vacuum detection at the receive position with the handler Up.
Normal receipt prepares IPM Down and Repeat prepares IPM Up. A contradictory
or unknown endpoint is unavailable. Forward release checks recipient holding and
Supply's ready, stopped axes and Unrotated feedback before each actuator.
Return receipt rechecks these conditions after gripper closure before advancing the fixer.
After release, Placement returns Z
to standby with its handler cylinder still Up, then moves Y to the selected heat sink while retaining handoff X.
Supply waits for Placement's `Clear` after that Y move before its XY return.
`WaitingForPlacementClear` covers this Z/Y departure wait until Placement publishes `Clear`.
`HandingOff` owns both the wait for Placement holding and the fixer/gripper release;
there is no separate release state.

Handoff requires the completed give-position command, settled axes and current rotation/grip feedback.
Moving axes manually to matching coordinates does not complete the handoff process stage.
Only confirmed recipient holding permits Supply release. Shared-area overlap,
entry/exit checks and collision boundaries are not used.

Rotation and gripper positions come from current paired inputs. Both endpoint
inputs ON or both OFF mean Between. PCB detection alone does not prove holding:
`Secured` also requires closed-gripper and IPM-fixer feedback. Output commands
never substitute for endpoint confirmation.

Both normal and Repeat handoff reject Rotated or Between feedback. Normal operation
stops with an interlock error if this is detected at give XYZ. Release rechecks
Unrotated feedback before opening the gripper. Withdrawal waits for Placement to
clear before rotating at handoff XYZ. Repeat reverse preparation approaches at
Travel Z and confirms Unrotated at handoff XYZ, even when the coordinates already match. If Placement is
already holding or returning the PCB at receive Z, invalid rotation stops the
operation without automatically rotating Supply.

Manual axis moves and jog retain the current Z. A handoff point move accepts
either confirmed orientation and preserves it; a pickup point move requires Rotated.

## SMEMA and slot progress

The local `PickStep` tracks PCB 1, PCB 2, and WaitingForCarrierExit for the current
upstream carrier during the active run. A new START resets it to PCB 1.
Ready stays ON through both pickup checks and the last pickup lift to Travel Z.
It then falls to tell the upstream equipment that pickup is complete; Placement
need not have received the last PCB yet.

Board Available OFF resets the next slot to PCB 1, including departure while the
last PCB is still being handed off. A stale ON cannot start another
carrier. If availability disappears during a pickup, cancel that pickup; a late
completion cannot advance a replacement carrier. If the gripper or fixer has not
released, loss of availability stops with an error rather than selecting an empty
pickup. During one run, the next carrier still requires that OFF edge.
In automatic mode, STOP
preserves Ready while the upstream carrier remains available.

In teaching/manual mode, FRONT 1 `TEST Available` replaces the real SMEMA input.
The selector contact is ON in teaching/manual mode. Mode changes clear TEST.
Automatic SMEMA output calls do not write in teaching; direct manual output
control remains available. TEST OFF followed by ON starts the next carrier.

## Repeat and verification

The same switch in `PcbSupplier.cs` owns reverse receipt, return placement and
re-pickup. With Placement disabled, Supply initially picks one PCB using the
normal upstream handshake, visits handoff, and then repeats with that PCB.
With Placement enabled, Placement returns each PCB from its original heat sink;
Heat Sink 1 maps to Pickup 1 and Heat Sink 2 maps to Pickup 2. One PCB completes
its round trip before the next one starts.

Supply closes its gripper and advances the fixer while Placement still holds the
PCB. After Placement releases and clears the handoff, Supply rotates at handoff
XYZ, rises to Travel Z, moves to the selected pickup XY, and descends to pickup Z.
It retracts the fixer, opens the gripper, rises empty to Travel Z, and uses normal
pickup to grip the same PCB again. The normal forward handoff returns it to its
original heat sink.

After each forward handoff, Supply waits for Placement to clear, then runs the
same withdrawal as normal production: rotate at handoff XYZ -> Travel Z -> pickup
XY. Repeat waits there with its gripper and fixer released. The slot remains the
PCB just handled; the next return request selects the corresponding slot before
receipt. Initial standby is Pickup 1 XY + Travel Z. A return request cannot bypass
unfinished withdrawal within the same run. The next receipt approaches at
Travel Z and unrotates only after reaching handoff XYZ. When the last station
finishes, the machine lets empty withdrawal complete before cancelling the
forward units and reversing the main conveyor.

SMEMA Board Available is not a support sensor. Repeat placement and re-pickup do
not wait for it or cancel when it is OFF. This path has no support-presence input:
release occurs at the taught pickup XYZ even if no physical support is present.
The virtual model represents fixed supports at those pickup positions.

After STOP, remove the PCB from Supply before START, except for a normal-mode PCB
already secured at the completed forward handoff. START checks ready axes,
Unrotated feedback, gripper and IPM fixation, then reuses `MovingToHandoff`:
Travel Z -> handoff XY -> handoff Z -> rotation confirmation. Handoff stays
unavailable throughout this move. After arrival, Supply holds the PCB until
Placement confirms holding. No coordinate comparison is used to admit this restart.
The next pickup scan always starts at PCB 1, including after this handoff; the
previous slot is not resumed. Partial release, interrupted travel and Repeat-held
PCBs still require removal. An empty new run moves to PCB 1 standby;
Repeat then waits for Placement's return request. Grip loss during the pickup lift
still stops the operation, and a missing returned PCB does not advance to another slot.

See the [Repeat instructions](../../docs/STATION3_COMMISSIONING.md#repeat).

Focused regressions cover standby before SMEMA, both pickup slots, rotation at
handoff XYZ, travel at a different Travel Z, motion cancellation,
recipient holding feedback, independent departure, and recorded XYZ teaching.
