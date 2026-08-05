# PCB Supply Handler

This document is the behavior contract for `PcbSupplyHandler`.
It records the agreed machine behavior while the automatic state logic is
implemented incrementally.

Keep handler-specific behavior beside its project. Future handlers and stations
should use the same sections: responsibility, observed state, compound
operations, normal flow, and behavior not yet defined.

## Responsibility

The supply handler:

- receives a two-PCB carrier from the upstream machine through SMEMA;
- checks PCB 1 and PCB 2 independently;
- picks a detected PCB with its pneumatic gripper;
- rotates the held PCB toward the buffer stage;
- places the PCB at the taught supply buffer position; and
- releases the upstream carrier after both PCB positions have been checked.

PCB position selection is work progress, not physical handler state:

```csharp
public enum PcbSupplySlot
{
    Pcb1,
    Pcb2,
}
```

## Buffer boundary

The supply and placement handlers do not exchange a PCB directly:

```text
Supply picks and rotates PCB
  -> Supply places PCB on Buffer Stage and leaves
  -> Placement picks PCB from Buffer Stage
```

There is no project reference between `IBTM.PcbSupply` and
`IBTM.Stations.PcbPlacement`, and neither project references
`IBTM.PcbBuffer`. The host's `PcbBufferService` owns the shared
[`BufferStage`](../IBTM.PcbBuffer/DESIGN.md) transaction and passes its
cancellation token to the selected handler.

The Buffer PCB-present input is confirmed. Additional Buffer actuators and
clearance inputs have not been confirmed yet.

## Observed state

The automatic trigger is based on digital inputs. An output value is never used
as proof that a pneumatic actuator reached its commanded position.

The supply state only describes the two confirmed rotation endpoints:

```csharp
public enum PcbSupplyRotation
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

`Unrotated` and `Rotated` describe orientation, not the handler's X/Z
coordinates.

The PCB-presence sensor is independent of the gripper. The PCB input determines
whether a PCB is present. The gripper-closed input only confirms that the
gripper actuator closed.

Every relevant input change re-evaluates the behavior of the current state.
PCB/gripper input combinations are branch conditions inside a state, not
additional enum values.

## Compound rotation operation

Rotation in either direction is one handler operation. Callers must not command
Safe Z and the pneumatic rotation separately.

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
  -> X home outside the machine
  -> upper Z home
```

Rotation completes before Z starts moving. X homing at the lower Z limit is a
narrow homing exception to the normal Safe Z rule.

Rotate:

```text
Safe Z
  -> rotation output ON
  -> rotated input ON
```

Unrotate:

```text
Safe Z
  -> rotation output OFF
  -> unrotated input ON
```

The reverse rotation uses the same Safe Z rule. There is no separate X clearance
teaching point.

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

Supply pickup does not wait for the Buffer or Placement handler. While the Buffer
is occupied or Placement is using it, Supply may pick the next PCB, move to Safe Z,
rotate, and wait while holding the PCB. Buffer ownership is requested
only when Supply is ready to enter and place the held PCB.

PCB 1 must not start again from a stale Board Available signal left by the
previous carrier. A completed carrier must finish its Board Available cycle
before the next PCB 1 cycle is accepted.

## Check and pick

The handler moves to the selected PCB detection position and checks the
dedicated PCB sensor before closing the gripper.

PCB detected:

```text
Move to selected PCB position
  -> PCB input ON
  -> close gripper
  -> gripper-closed input ON
  -> Safe Z
  -> rotate
  -> move to the taught supply buffer position
```

PCB 1 not detected:

```text
PCB 1 input OFF
  -> do not close the gripper
  -> Safe Z
  -> move to PCB 2
```

PCB 2 not detected:

```text
PCB 2 input OFF
  -> do not close the gripper
  -> Safe Z
  -> mark the upstream carrier complete
  -> upstream Machine Ready ON
  -> reset the next slot to PCB 1
  -> wait for the next carrier
```

No rotation or buffer transfer occurs when PCB 2 is not detected. The supply
handler remains `Unrotated`.

## PCB 1 and PCB 2 after rotation

After PCB 1 is picked and rotated, the upstream carrier remains in place because
PCB 2 has not been checked.

After PCB 2 is picked and rotated:

```text
mark the upstream carrier complete
  -> upstream Machine Ready ON
  -> reset the next slot to PCB 1
  -> move the held PCB to the taught supply buffer position
```

The second-carrier SMEMA flow may proceed while the supply handler finishes the
PCB 2 buffer placement.

## Rotated behavior

The confirmed high-level `Rotated` flow is:

```text
Supply holds PCB
  -> wait while Buffer is occupied or owned
  -> acquire Buffer ownership after both handlers are clear
  -> move to Supply Buffer position
  -> place PCB on Buffer Stage
  -> open Supply gripper
  -> Safe Z
  -> X origin outside the machine
  -> unrotate
  -> Supply leaves the Buffer Stage
```

The exact input that proves a successful buffer placement and the condition that
proves Supply has cleared the buffer are not defined yet. Placement must not
depend on Supply directly; it starts from the confirmed Buffer Stage condition.

After the PCB 1 buffer placement and unrotation, check PCB 2 on the same carrier.
After the PCB 2 buffer placement and unrotation, wait for the next accepted SMEMA carrier and
start again from PCB 1.

## Not defined yet

The current design intentionally covers the normal machine flow only. Automatic
recovery is not yet defined for:

- PCB input OFF while the gripper-closed input is ON;
- both rotation-position inputs being ON; or
- application startup while the rotation state is `Between`;
- Buffer Stage PCB-presence and clearance inputs; or
- any Buffer Stage clamp, lift, or other actuator.

Do not add speculative recovery or defensive branches until the real machine
behavior is confirmed.
