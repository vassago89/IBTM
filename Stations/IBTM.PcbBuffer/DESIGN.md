# PCB direct handoff

There is no physical buffer. Supply holds the PCB while Placement takes it.
`BufferStage` retains the existing collision coordinates and reads current motion
and `IPcbHandoffState` feedback from both handlers. It stores no PCB owner or presence.
The historical Buffer names in stored settings remain to preserve taught positions.
The former buffer input is unmapped; its enum slot is reserved to keep other I/O IDs stable.

1. Supply secures the PCB with its gripper and IPM fixer, rotates and moves to its taught give X/Y at Rotation Z. It keeps that height while holding the PCB; there is no handoff descent.
2. Placement may enter only while Supply is settled there and its PCB holding feedback is confirmed.
3. Placement lowers to its taught pose, detects the PCB, applies vacuum and closes its IPM gripper.
4. Supply requires all three recipient signals before retracting its fixer and again before opening its gripper.
5. Supply moves to Clear Z and exits in X with Y unchanged. Placement stays at handoff until Supply is outside.
6. Placement raises its handler and Z, keeping IPM down, and carries the PCB to the heat sink.

Below Placement Entry Z, one handler must remain settled at its taught pose while
the other enters or exits. Other overlap remains a collision fault. Both handlers
must be enabled with known, homed feedback before entry. Missing Supply feedback
is not proof that it has left.
Supply handoff recognition reads the current Rotation Z setting and settled X/Y/Z
feedback. A matching X/Y at a different height does not permit Placement entry.

STOP retains no synthetic handoff step. Current holding and position feedback
determine the next action. A remaining release requires recipient holding again;
two fully retracted Supply actuators permit finishing the empty exit. Loss of Supply
holding feedback inside the zone before its handoff pose stops automatic operation.
Manual entry/rotation restrictions still apply.
