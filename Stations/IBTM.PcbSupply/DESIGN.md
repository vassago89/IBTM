# PCB Supply Handler

This is the behavior contract for `PcbSupplyHandler` and `PcbSupplier`.
The confirmed sequence uses separate rotation and handoff heights.

## Responsibility

- Pick PCB 1 and PCB 2 from the same upstream carrier.
- Secure each PCB with the supply gripper and IPM fixer.
- Pick while Rotated, then unrotate at Rotation Z and approach Placement at the taught give Z.
- Hold the PCB until Placement confirms its receiving position and holding inputs.
- Complete the upstream SMEMA handshake after both pickup positions are checked.

## Coordinates

| Teaching item | Use |
| --- | --- |
| Rotation Z | Standby, pickup XY travel, and rotation in either direction |
| PCB Pickup Common Y | Shared pickup Y for PCB 1 and PCB 2 |
| PCB 1 Pick X/Z | First pickup position |
| PCB 2 Pick X/Z | Second pickup position |
| PCB Give Position XYZ | Handoff Z followed by handoff XY; also the return travel height |

Standby is PCB 1 X, common pickup Y, and Rotation Z, with Rotated feedback confirmed.
There is no separate standby, Clear Z, return coordinate, or collision boundary.
The two pick X/Z values belong to the recipe; the other values are machine settings.

`PCB Give Position` is staged in Teaching and committed with `Apply & Save Handoff`.
It now stores Z independently of Rotation Z. Older settings that stored only X/Y
must have give Z taught before equipment operation; do not infer it from Rotation Z.

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
  -> give Z
  -> give X/Y together, keeping give Z
  -> wait for Placement to secure the PCB at its receiving XYZ
  -> retract IPM fixer
  -> open supply gripper
  -> wait for Placement Handler Up
  -> next pickup X/Y together, keeping give Z
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
`MovingToHandoff` reaches give Z before any XY approach. The motion call receives
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
| Supply `Holding` | Secured PCB at settled give XYZ | Placement moves Z to its receive height with its cylinder Up |
| Placement `Holding` | At receive XY/Z, handler Up, PCB detected, vacuum and gripper confirmed | Supply retracts fixer, then opens gripper |
| Supply `Released` | At give XYZ, fixer and gripper released | Placement returns Z to standby |
| Placement `Clear` | Receiving/travel preparation complete, or working at the heat sink | Supply returns to pickup |
| Either unit `Unavailable` | Disabled or not at a confirmed handoff condition | Peer waits |

`MachineController` passes Placement's handoff interface into Supply's run.
Placement receives Supply's handoff interface through DI. Neither project refers
to the other. Each loop listens for the peer's own changes to wake its wait;
it never relays those changes back to the peer.
Supply and Placement approach independently. Every Placement axis movement
requires its handler cylinder Up. Either may arrive first. Both handlers must settle at their
own taught standby/give XYZ before Placement moves Z to `ReceiveZ`.

Supply releases only while Placement reports handoff `Holding`, which
requires PCB detection, vacuum detection, and its closed IPM gripper. The state
is checked again before each release actuator. After release, Placement returns Z
to standby with its handler cylinder still Up. Supply waits for Placement's `Clear`
before its XY return. Placement may then leave for the selected heat sink.

The give position requires settled X/Y/Z feedback at the stored give XYZ;
matching X/Y at Rotation Z is insufficient when the two heights differ.
Only confirmed recipient holding permits Supply release. Shared-area overlap,
entry/exit checks and collision boundaries are not used.

Rotation and gripper positions come from current paired inputs. Both endpoint
inputs ON or both OFF mean Between. PCB detection alone does not prove holding:
`Secured` also requires closed-gripper and IPM-fixer feedback. Output commands
never substitute for endpoint confirmation.

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

Repeat uses PCBs already seated on the main-conveyor carrier. Supply does not
feed new PCBs during Repeat. See the [Repeat instructions](../../docs/STATION3_COMMISSIONING.md#repeat).

Focused regressions cover standby before SMEMA, both pickup slots, rotation at
Rotation Z, travel at a different give Z, interrupted entry at that same height,
recipient holding feedback, independent departure, and staged XYZ teaching.
