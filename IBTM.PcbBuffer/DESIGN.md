# PCB Buffer

`BufferStage` reads the Buffer PCB-present input and current axis positions. It
does not own a lock, owner, or stored occupancy state.

The collision ranges and both handoff positions are taught values. Supply and
Placement normally cannot enter each other's Buffer area. The one permitted
overlap is the physical PCB handoff:

```text
Supply stops at its handoff position with the IPM fixer forward
  -> Placement enters and stops at its handoff position
  -> Placement vacuum and IPM gripper inputs turn on
  -> Supply retracts the IPM fixer
  -> Supply retracts the Nest
  -> Supply moves down to Clear Z and exits in X
  -> Placement exits with the PCB assembly
```

While Placement enters, Supply must remain at its taught handoff position.
While Supply exits, Placement must remain at its taught handoff position. Any
other simultaneous overlap is a Buffer conflict.

A handler is at Handoff only when every handler axis reports In Position, the
motion command has ended, and X/Y/Z are within 0.05 mm of the taught position.
Small stopped-position vibration therefore does not require exact coordinate
equality.

Supply and Placement processes do not read the Buffer PCB input directly. They
use `PcbPresent`, `CanSupplyEnter`, `CanPlacementEnter`, and `WaitForPcbAsync`
from this object.

Manual teaching is stricter than automatic handoff: a handler cannot be moved
manually while the other handler is inside the Buffer area.

At startup no Buffer state is restored from a file or memory. Homed axis
positions and live inputs are the only source of truth.
