# PCB Supply Handler

This document is the behavior contract for `PcbSupplyHandler` and
`PcbSupplier`.

Keep handler-specific behavior beside its project. Future handlers and stations
should use the same sections: responsibility, observed state, compound
operations, normal flow, and behavior not yet defined.

## Responsibility

The supply handler:

- receives a two-PCB carrier from the upstream machine through SMEMA;
- checks PCB 1 and PCB 2 independently;
- fixes a detected PCB/IPM assembly with the IPM fixing cylinder;
- rotates the held PCB toward the shared handoff area;
- hands the held PCB to Placement at the taught handoff X/Y, keeping Rotation Z; and
- releases the upstream carrier after both PCB positions have been checked.

The upstream carrier transaction uses one local step, separate from the live
handler state:

```csharp
private enum PickStep
{
    Pcb1,
    Pcb2,
    WaitingForCarrierExit,
}
```

The live handler and Buffer states always take priority over this step. `Pcb1`
and `Pcb2` only select the source position. After the PCB 2 check,
`WaitingForCarrierExit` keeps the existing Board Available signal from being
mistaken for a new carrier. Board Available OFF returns the step to `Pcb1`.
STOP retains completed slot checks for the same carrier. Board Available OFF
resets the step even while stopped. If it occurs during a pickup, that pickup
is canceled and its late completion cannot advance the replacement carrier.
On restart, current handler and Buffer feedback still take priority over the slot step.
STOP also preserves the existing Ready output while Board Available is ON.
`StopUpstream` turns Ready OFF only when no carrier is available. A failed input
read is reported and leaves Ready unchanged; it is not treated as an empty station.

## Horizontal movement

Supply uses coordinated X/Y movement for pickup, handoff and return travel. The shared
`IXyMotion.MoveToXYAsync` first reaches Rotation Z, then moves both horizontal
axes together. Supply must be rotated before handoff entry. Pickup reaches the
selected PCB X and Carrier Y together before descending to the pick Z.

After both release actuators are confirmed retracted, Supply waits for Placement's
handler cylinder Up feedback and returns directly to the next pickup X and Carrier Y
at Rotation Z. After PCB 1 this is PCB 2; after PCB 2 it is PCB 1 of the next carrier.
There is no separate exit Z or return coordinate. Homing still moves X before homing Y.

## Direct handoff

The historical Buffer names below refer to the shared collision zone and saved
coordinates, not a physical staging buffer.

Supply and Placement approach independently while Placement's handler cylinder is Up.
Either can arrive first. Placement waits at its taught receiving XYZ and lowers only
the cylinder after both handlers are settled at their taught handoff positions.
Placement secures the PCB with vacuum and the loose IPM with its IPM gripper.
After PCB detection, vacuum and gripper-closed feedback are all confirmed at the taught handoff position, Supply
retracts its IPM fixer and opens its gripper. Once both retracted endpoints are confirmed,
Placement raises its handler cylinder while holding its XYZ. Supply waits for confirmed Up
before its XY return; Placement waits until Supply is outside before moving its axes. See
[`BufferStage`](../IBTM.PcbBuffer/DESIGN.md).

There is no physical buffer. Supply holds the PCB until Placement secures it; no buffer-present input is used.

## Observed state

The automatic trigger is based on digital inputs. An output value is never used
as proof that a pneumatic actuator reached its commanded position.

Rotation, Gripper, IPM fixer, and PCB state are calculated from the current inputs.
They are not retained in process memory. Rotation has three values:

```csharp
public enum PcbSupplyRotationState
{
    Unrotated,
    Between,
    Rotated,
}
```

| State | Confirmed inputs |
| --- | --- |
| `Unrotated` | Unrotated input ON, rotated input OFF |
| `Between` | Both rotation-position inputs OFF or both ON |
| `Rotated` | Unrotated input OFF, rotated input ON |

`Unrotated` and `Rotated` describe orientation, not the handler's X/Y/Z
coordinates.

Gripper and IPM fixer each use `Backward`, `Between`, and `Forward`. `Between`
also covers an invalid pair where both endpoint inputs are ON, so it is never
mistaken for either completed endpoint.
`PcbReleased` requires both mechanisms to be `Backward`; PCB detection OFF or
an output command alone does not permit Placement to lift or Supply to leave.

The PCB-detection sensor is independent of the Gripper and IPM fixer. Its input only
means that a PCB is within the sensor's detection range; it does not prove that
the handler holds the PCB. The live PCB state is:

| State | Confirmed inputs |
| --- | --- |
| `None` | PCB detection OFF |
| `Detected` | PCB detection ON without both fixing mechanisms forward |
| `Secured` | PCB detection, Gripper forward, and IPM fixer forward ON |

`PcbSupplyHandler` is the only production class that reads Supply IO. Placement
does not read Supply PCB, Gripper, or IPM-fixer inputs directly. Both automatic units use
the live handoff state published by `BufferStage`.

Every relevant input change re-evaluates the behavior of the current state.

## Compound rotation operation

Rotation in either direction is one handler operation. Callers must not command
Rotation Z and the pneumatic rotation separately.

The recovery-only move to the lower Z positive limit is permitted only while the
rotated-position input is confirmed. An output command is not accepted as proof of
rotation.

Initial homing uses one supply-specific path. Homing is unavailable while the
rotation is `Between`, or for this exact material condition:

```text
Unrotated AND Supply PCB present
```

An empty unrotated handler first commands rotation and waits for the rotated input.
An already rotated handler starts from the next step:

```text
rotated input ON
  -> Z positive limit
  -> X/Y home outside the machine
  -> upper Z home
```

Rotation completes before Z starts moving. X homing at the lower Z limit is a
narrow homing exception to the normal Rotation Z rule.

Rotate:

```text
Rotation Z
  -> rotation output ON
  -> rotated input ON
```

Unrotate:

```text
Rotation Z
  -> rotation output OFF
  -> unrotated input ON
```

The reverse rotation uses the same Rotation Z rule.

The Supply path uses two Y coordinates. PCB 1 and PCB 2 share the upstream
carrier Y because they are arranged horizontally; the Buffer has its own Y.

Machine teaching values:

```text
Rotation Z
Carrier Y
PCB Give X/Y (at Rotation Z)
Supply collision boundary 1 / 2
Placement collision boundary 1 / 2
```

Recipe teaching values:

```text
PCB 1 Pick X/Z
PCB 2 Pick X/Z
```

There is no separate Supply handoff Z and no lowering before the handoff.
Rotation Z is shared by transport, rotation and the held handoff. Old saved
`BufferHandoffPosition.Z` values are ignored; the stored position now contains only X/Y.
Positive Z points downward. Release and XY return also use Rotation Z; the handler
remains rotated until the return completes. The former `BufferClearZ` is ignored
on load and is no longer saved or taught. Supply has three Z values: Rotation Z
and the two PCB pick Z values.

While Supply is inside its Buffer collision range, manual Z and rotation
commands are disabled; X/Y adjustment is permitted at Rotation Z when Placement
is outside the shared area. A manual point move to the Buffer requires the rotated
input; a PCB pick point requires the unrotated input. Automatic XY withdrawal
requires both Supply mechanisms retracted and Placement's handler cylinder Up.

## SMEMA and carrier ownership

For commissioning, the Operation page's FRONT 1 card exposes `TEST Available`.
`UpstreamCarrierAvailable` uses only `TestUpstreamCarrierAvailable` in teaching,
and only the real input in automatic mode. The physical `AutoMode` mapping key
is retained; its contact is ON in teaching/manual mode. Turning teaching OFF
clears TEST; turning it back ON does not restore the old value.
The test value is memory only, defaults to OFF on a new application session,
and survives STOP while teaching remains ON. It does not change the DI value or create PCB sensor feedback.
After both slot checks/pickups are complete and Supply is back at Rotation Z,
turn TEST OFF and then ON for the next carrier. Real Available does not affect
this teaching cycle. Test changes notify the existing
pickup loop, including cancellation if availability is removed during pickup.
The selector must have valid feedback to select the source; general I/O faults
still stop the machine.

Sequence SMEMA outputs use `IIoService.SetAutomaticSmemaOutput`, which does not
write in teaching. Initialization and entry to teaching clear existing SMEMA
outputs through the normal STOP path. The OUTPUTS window retains direct
`SetOutput` access and can manually turn SMEMA ON/OFF in teaching; no new
restriction is applied to direct output control.

PCB 1 starts with a new upstream SMEMA handshake:

```text
Unrotated and idle
  -> upstream Machine Ready ON
  -> wait for upstream Board Available ON
  -> keep upstream Machine Ready ON
  -> select PCB 1
  -> run the PCB 1 check/pick operation
```

PCB 2 belongs to the same upstream carrier. After PCB 1 has been handed to Placement and
the supply handler is unrotated again, PCB 2 is checked without waiting for
another SMEMA handshake.

The equipment contract confirmed on 2026-09-15 keeps Ready ON through both PCB
checks/pickups. Ready turns OFF only after the last pickup is secured (or the
slot is empty) and Supply has returned to Rotation Z. Do not use carrier arrival
or STOP as pickup completion. Placement receipt of the last PCB is not required.

The Board Available OFF transition completes `WaitingForCarrierExit` even if it occurs
while the handler is still moving the second PCB to the Buffer. The running unit
then sets Ready ON for the next carrier. A following Available ON is accepted as
the next carrier after the current physical move is resolved.

Supply pickup does not wait for the Placement handler. If Placement is inside
the shared zone without confirmed handler-cylinder Up feedback, Supply may pick the next PCB,
raise to Rotation Z, rotate, and wait while holding it. It moves to the taught
Buffer X/Y together only after the Buffer entry condition is satisfied, keeping
Rotation Z throughout the handoff. Confirmed Placement cylinder Up permits entry
without waiting for Placement's XYZ arrival; either handler may arrive first.

PCB 1 must not start again from a stale Board Available signal left by the
previous carrier. A completed carrier must finish its Board Available cycle
before the next PCB 1 cycle is accepted.

## Check and pick

The handler moves to the selected PCB detection position and checks the
dedicated PCB sensor before advancing the IPM fixer.

PCB detected:

```text
Rotation Z
  -> selected PCB X and Carrier Y together
  -> selected PCB Z
  -> PCB input ON
  -> Supply Gripper forward
  -> Supply Gripper-forward input ON
  -> IPM fixer forward
  -> IPM fixer-forward input ON
  -> Rotation Z
  -> rotate
  -> move to Buffer X/Y together
  -> hold at Rotation Z for Placement
```

PCB 1 not detected:

```text
PCB 1 input OFF
  -> do not move the IPM fixer
  -> Rotation Z
  -> move to PCB 2
```

PCB 2 not detected:

```text
PCB 2 input OFF
  -> do not move the IPM fixer
  -> Rotation Z
  -> mark the upstream carrier complete
  -> upstream Machine Ready OFF
  -> wait for the existing Board Available signal to turn OFF
  -> upstream Machine Ready ON
  -> wait for the next carrier
```

No rotation or buffer transfer occurs when PCB 2 is not detected. The supply
handler remains `Unrotated`.

## PCB 1 and PCB 2 after pickup

After PCB 1 is picked, the upstream carrier remains in place because PCB 2 has
not been checked.

After the PCB 2 check/pick operation has returned to Rotation Z:

```text
mark the upstream carrier complete
  -> upstream Machine Ready OFF
  -> rotate and move the held PCB toward the taught supply handoff position
```

In parallel, wait for Board Available OFF and set Ready ON for the next carrier.
The next-carrier handshake may proceed while Supply finishes the PCB 2 handoff.

## Rotated behavior

The confirmed high-level `Rotated` flow is:

```text
Supply PCB detected, Gripper forward, and IPM fixer forward
  -> Rotation Z
  -> rotate
  -> wait while Placement blocks Buffer entry
  -> Buffer X/Y together
  -> wait for Placement PCB, vacuum, and IPM-gripper inputs
  -> retract Supply IPM fixer
  -> retract Supply Gripper
  -> confirm both retracted endpoints
  -> Placement raises its handler cylinder at the receiving XYZ
  -> wait for confirmed Placement Handler Up
  -> next PCB pickup X and Carrier Y together, keeping Rotation Z
  -> unrotate
```

Supply stays at its taught handoff pose holding the PCB until Placement reports
PCB detected, vacuum detected and IPM gripper closed at its own taught pose.
Supply checks these conditions before retracting each holding actuator. Placement
waits for both Supply release endpoints before lifting, then waits for known Supply X
feedback outside the collision range before moving its axes.

During the current run, each release requires Placement holding feedback.
After both actuators retract, Supply exits once Placement Handler Up is confirmed. Supply
horizontal motion with Placement inside the shared area and its cylinder not Up
is a collision fault. Loss of holding feedback before reaching the
handoff pose is an error, not permission to release.

STOP discards the run's slot progress without imposing an empty-machine START block.
A new run evaluates current holding, position and buffer feedback; it does not replay
a saved entry/release phase. Handoff still requires settled X/Y and actual
Z at Rotation Z. There is no buffer-presence condition or handoff descent.

After the PCB 1 handoff and unrotation, check PCB 2 on the same carrier.
After the PCB 2 handoff and unrotation, wait for the next accepted SMEMA carrier and
start again from PCB 1.

## Repeat / Dry Run

Repeat uses the automatic unit loops and returns the carrier from the last enabled
NG unit to the main entry sensor. Station 1 seats and raises the carrier normally.
When Placement is enabled, it picks each existing PCB from its heat sink, carries it
to the existing handoff position, and returns it to the same heat sink for placement
and pressing. Supply does not feed a new PCB during Repeat.

For a carrier-only check, disable Placement, Bolt Fastening and Inspection.
See the [Repeat instructions](../../docs/STATION3_COMMISSIONING.md#repeat).

## Not defined yet

Automatic recovery is not yet defined for:

- PCB input OFF while the IPM fixer-forward input is ON;
- both rotation-position inputs being ON; or
- application startup while the rotation state is `Between`.

Do not add speculative recovery or defensive branches until the real machine
behavior is confirmed.
