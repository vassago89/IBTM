# PCB direct handoff

There is no physical buffer. Supply holds the PCB while Placement takes it.
`BufferStage` retains the existing collision coordinates and reads current motion
and `IPcbHandoffState` feedback from both handlers. It stores no PCB owner or presence.
The historical Buffer names in stored settings remain to preserve taught positions.
The former buffer input is unmapped; its enum slot is reserved to keep other I/O IDs stable.

1. Supply secures the PCB with its gripper and IPM fixer, rotates and moves to its taught give X/Y at Rotation Z. It keeps that height while holding the PCB; there is no handoff descent.
2. Placement independently raises its handler cylinder, moves Z to its taught receiving height, prepares the receiving orientation and IPM, and moves XY to the receiving position. Receiving, horizontal travel and rotation use this same Z. Either handler may arrive first. The taught paths must clear each other with the Placement handler cylinder Up; no extra standby position is stored.
3. Placement waits at receiving XYZ with its cylinder Up. After both handlers are settled at their taught poses and Supply holding feedback is confirmed, Placement lowers only its handler cylinder, detects the PCB, applies vacuum and closes its IPM gripper. Its axes do not move during receipt.
4. Supply requires all three recipient signals before retracting its fixer and again before opening its gripper.
5. Once both Supply mechanisms report fully retracted, Placement raises its handler cylinder, keeping IPM down and its XYZ unchanged.
6. Supply waits for confirmed Placement Handler Up, then returns in coordinated XY at Rotation Z to the next PCB pickup X and Carrier Y: PCB 2 after PCB 1, and the next carrier's PCB 1 after PCB 2. There is no Clear Z or separate return position.
7. Placement holds its XYZ until Supply is outside, then carries the PCB to the heat sink at its common Z.

Placement handler Up feedback permits independent approach in the shared area;
Z height alone does not prove cylinder clearance. With the cylinder not Up,
Supply horizontal motion is a collision fault while both handlers are inside.
One handler must remain settled at its taught pose for other receiving overlap.
Other overlap remains a collision fault. Both handlers
must be enabled with known, homed feedback before entry. Missing Supply feedback
is not proof that it has left.
Supply handoff recognition reads the current Rotation Z setting and settled X/Y/Z
feedback. A matching X/Y at a different height does not permit cylinder descent.
Placement uses `BufferHandoffPosition.Z` for receiving, horizontal travel and rotation.
The former separate `BufferEntryZ` is ignored on load and is no longer saved or taught.
Only the two heat-sink placement Z values remain separate from this common height.

STOP retains no synthetic handoff step. Current holding and position feedback
determine the next action. A remaining release requires recipient holding again;
two fully retracted Supply actuators and confirmed Placement Handler Up permit
finishing the empty exit. Loss of Supply
holding feedback inside the zone before its handoff pose stops automatic operation.
Manual entry/rotation restrictions still apply.
