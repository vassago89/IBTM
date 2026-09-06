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
- rotates the held PCB toward the buffer stage;
- places the PCB at the taught Buffer X/Y and Place Z; and
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
Stop does not retain the step, so Start begins from PCB 1 after resolving the
current physical state.

## Horizontal movement

Supply X and Y never move together. At Rotation Z, Supply rotates before Buffer
entry moves Y and then X. Buffer
exit moves X to its home position while Y remains fixed. Homing moves X before
homing Y. The project receives `IAxisMotion`, so
coordinated XY commands are not available to Supply code.

## Buffer handoff

Supply and Placement overlap only at their taught Buffer handoff positions.
Placement secures the PCB with vacuum and the loose IPM with its IPM gripper.
After both feedback inputs are confirmed at the taught handoff position, Supply
retracts its IPM fixer and leaves the Buffer. Placement waits until Supply is
outside before leaving. See
[`BufferStage`](../IBTM.PcbBuffer/DESIGN.md).

The Buffer PCB-present input is confirmed. Additional Buffer actuators and
clearance inputs have not been confirmed yet.

## Observed state

The automatic trigger is based on digital inputs. An output value is never used
as proof that a pneumatic actuator reached its commanded position.

Rotation, Nest, IPM fixer, and PCB state are calculated from the current inputs.
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
| `Between` | Both rotation-position inputs OFF |
| `Rotated` | Unrotated input OFF, rotated input ON |

`Unrotated` and `Rotated` describe orientation, not the handler's X/Y/Z
coordinates.

Nest and IPM fixer each use `Backward`, `Between`, and `Forward`. `Between`
also covers an invalid pair where both endpoint inputs are ON, so it is never
mistaken for either completed endpoint.

The PCB-detection sensor is independent of the Nest and IPM fixer. Its input only
means that a PCB is within the sensor's detection range; it does not prove that
the handler holds the PCB. The live PCB state is:

| State | Confirmed inputs |
| --- | --- |
| `None` | PCB detection OFF |
| `Detected` | PCB detection ON without both fixing mechanisms forward |
| `Secured` | PCB detection, Nest forward, and IPM fixer forward ON |

`PcbSupplyHandler` is the only production class that reads Supply IO. Placement
does not read Supply PCB, Nest, or IPM-fixer inputs directly. Both automatic units use
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
Unrotated AND (Supply PCB present OR Buffer PCB present)
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
Buffer Handoff X/Y/Z
Buffer Clear Z
Supply collision boundary 1 / 2
Placement collision boundary 1 / 2
```

Recipe teaching values:

```text
PCB 1 Pick X/Z
PCB 2 Pick X/Z
```

Positive Z points downward, so Clear Z must be greater than Place Z. The only
horizontal movement below Rotation Z is the X-only exit from Buffer to the X home
position. Buffer Y stays fixed and the handler remains rotated at the confirmed
Clear Z.

While Supply is inside its Buffer collision range, manual Y, Z, and rotation
commands are disabled. A manual point move to the Buffer requires the rotated
input; a PCB pick point requires the unrotated input. The release-and-exit
operation above is the only automatic path that lowers Z and moves out of that
range.

## SMEMA and carrier ownership

PCB 1 starts with a new upstream SMEMA handshake:

```text
Unrotated and idle
  -> upstream Machine Ready ON
  -> wait for upstream Board Available ON
  -> upstream Machine Ready OFF
  -> select PCB 1
  -> run the PCB 1 check/pick operation
```

PCB 2 belongs to the same upstream carrier. After PCB 1 has been placed on the buffer and
the supply handler is unrotated again, PCB 2 is checked without waiting for
another SMEMA handshake.

The Board Available OFF transition completes `WaitingForCarrierExit` even if it occurs
while the handler is still moving the second PCB to the Buffer. A following ON is
therefore accepted as the next carrier after the current physical move is resolved.

Supply pickup does not wait for the Buffer or Placement handler. While a PCB is
detected at the Buffer or Placement blocks entry, Supply may pick the next PCB,
raise to Rotation Z, rotate, and wait while holding it. It moves to the taught
Buffer Y and then X only after the Buffer entry condition is satisfied, then
lowers Z to Handoff.

PCB 1 must not start again from a stale Board Available signal left by the
previous carrier. A completed carrier must finish its Board Available cycle
before the next PCB 1 cycle is accepted.

## Check and pick

The handler moves to the selected PCB detection position and checks the
dedicated PCB sensor before advancing the IPM fixer.

PCB detected:

```text
X home
  -> Carrier Y
  -> selected PCB X/Z
  -> PCB input ON
  -> Supply Nest forward
  -> Supply Nest-forward input ON
  -> IPM fixer forward
  -> IPM fixer-forward input ON
  -> Rotation Z
  -> rotate
  -> move to Buffer Y, then X
  -> move to Place Z
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
  -> upstream Machine Ready ON
  -> wait for the existing Board Available signal to turn OFF
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
  -> upstream Machine Ready ON
  -> wait for the existing Board Available signal to turn OFF
  -> rotate when a PCB was detected
  -> move the held PCB to the taught supply buffer position
```

The second-carrier SMEMA flow may proceed while the supply handler finishes the
PCB 2 buffer placement.

## Rotated behavior

The confirmed high-level `Rotated` flow is:

```text
Supply PCB detected, Nest forward, and IPM fixer forward
  -> Rotation Z
  -> rotate
  -> wait while Placement blocks Buffer entry
  -> Buffer Y
  -> Buffer X
  -> Place Z
  -> wait for Placement PCB, vacuum, and IPM-gripper inputs
  -> retract Supply IPM fixer
  -> retract Supply Nest
  -> move down to Clear Z
  -> X origin outside the machine with Y unchanged, clear of the Buffer
  -> Rotation Z
  -> unrotate
```

The Buffer PCB-present input proves that the PCB reached the handoff position.
Supply stays there until Placement secures the assembly. Supply observes that
live handoff state and performs its own release and exit operation. Placement
waits until Supply is physically outside the Buffer.

If Stop interrupts the release after either Supply cylinder has retracted,
restart repeats both retract commands and the Buffer exit from the current live
position. No recovery step is stored.

If Stop interrupts Buffer X entry, restart finishes X with Y unchanged. If it
interrupts the Handoff Z descent after the Buffer PCB input turns ON, the current
Handoff X/Y and Z between Rotation Z and Handoff Z identify the remaining descent.
It may continue only while Placement leaves that path clear. A new Buffer entry
still requires the Buffer PCB input OFF.

After the PCB 1 buffer placement and unrotation, check PCB 2 on the same carrier.
After the PCB 2 buffer placement and unrotation, wait for the next accepted SMEMA carrier and
start again from PCB 1.

## Not defined yet

The current design intentionally covers the normal machine flow only. Automatic
recovery is not yet defined for:

- PCB input OFF while the IPM fixer-forward input is ON;
- both rotation-position inputs being ON; or
- application startup while the rotation state is `Between`;
- automatic recovery after a Buffer PCB-present timeout;
- additional Buffer Stage clearance inputs; or
- any Buffer Stage clamp, lift, or other actuator.

Stop does not retain `PickStep`. Every Start begins at PCB 1 or waits for a
carrier, while current handler and Buffer state always take priority:

```text
Start
  -> resolve a held PCB first
  -> resolve a rotated or Buffer-area handler first
  -> otherwise begin the accepted carrier at PCB 1
```

After resolving the current physical state, the handler checks PCB 1 and PCB 2 from
live detection inputs. It does not restore or guess a previous software step.

Do not add speculative recovery or defensive branches until the real machine
behavior is confirmed.
