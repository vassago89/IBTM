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
  -> close supply gripper and confirm
  -> advance IPM fixer and confirm
  -> Travel Z
  -> PCB detection
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
Every slot is gripped and lifted before checking PCB presence at Travel Z.
Gripper, fixer and rotation feedback remain required during the lift; PCB detection
is required only after it. If no PCB is detected at Travel Z, retract the fixer,
open the gripper and advance to the next slot without rotating.
The second empty check completes the same handshake.

`MovingToPickup` prepares the PCB 1 standby position even when SMEMA is absent.
If rotation is required on startup, move Travel Z -> handoff XY -> handoff Z,
confirm Rotated there, then travel to standby.
`PrepareHandoffAsync` uses Travel Z -> handoff XY -> handoff Z, preserving the
current confirmed orientation during travel. It confirms Unrotated at handoff XYZ
before publishing a completed handoff. `MoveFromHandoffAsync` rotates at handoff
XYZ and moves through Travel Z to the selected pickup XY. Interrupted withdrawal
with confirmed Rotated feedback continues through Travel Z without rotating again.

Automatic rotation follows the awaited handoff move and waits for cylinder feedback;
it does not recheck handoff coordinates against the teaching tolerance.
`SetTeachingRotationAsync` applies only to the teaching rotation button. It never
moves an axis, requires homed, servo-on, settled XYZ at the configured handoff
position, and stops waiting if that condition is lost. The direct OUTPUTS toggle
uses the shared manual-output safety conditions without this coordinate restriction.

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
`Holding`. Placement monitors this condition through
its Z, X, then Y approach and cancels movement if Supply readiness is lost.
Every Placement axis movement requires its handler cylinder Up. Both handlers must settle at
their own taught standby/give XYZ before Placement moves Z to `ReceiveZ`.

Supply releases only while Placement reports handoff `Holding`, which
requires PCB detection and vacuum detection at the receive position with the handler Up.
Receipt prepares IPM Down. A contradictory
or unknown endpoint is unavailable. Forward release checks recipient holding and
Supply's ready, stopped axes and Unrotated feedback before each actuator.
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

Handoff rejects Rotated or Between feedback and stops with an interlock error.
Release rechecks Unrotated feedback before opening the gripper. Withdrawal waits
for Placement to clear before rotating at handoff XYZ.

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

## Restart and verification

After STOP, remove the PCB from Supply before START, except for a normal-mode PCB
already secured at the completed forward handoff. START checks ready axes,
Unrotated feedback, gripper and IPM fixation, then reuses `MovingToHandoff`:
Travel Z -> handoff XY -> handoff Z -> rotation confirmation. Handoff stays
unavailable throughout this move. After arrival, Supply holds the PCB until
Placement confirms holding. No coordinate comparison is used to admit this restart.
The next pickup scan always starts at PCB 1, including after this handoff; the
previous slot is not resumed. Partial release and interrupted travel still require PCB removal. A confirmed open gripper is not considered to hold
a PCB merely because the PCB detector or IPM fixer input is ON. On a new run,
the empty handler retracts its fixer and opens its gripper, awaiting feedback before
empty travel. Unknown gripper feedback still blocks a detected PCB at START.
An empty new run moves to PCB 1 standby. Grip loss during the pickup lift stops the operation.

The start review's `All stations to standby` uses `MoveToStandbyAsync` after Placement
has cleared the handoff. It shares the empty fixer/gripper preparation with START,
then follows Travel Z -> handoff XY/Z -> Rotated -> Travel Z -> PCB 1 XY.
Already Rotated feedback skips the handoff detour. It does not descend to pickup Z,
grip a PCB, or drive the upstream handshake. Held material must be cleared first;
work records are retained and HOME is required before this manual move.

Focused regressions cover standby before SMEMA, both pickup slots, rotation at
handoff XYZ, travel at a different Travel Z, motion cancellation,
recipient holding feedback, independent departure, and recorded XYZ teaching.
