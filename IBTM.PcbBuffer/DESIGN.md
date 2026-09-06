# PCB Buffer

`BufferStage` reads the Buffer PCB-present input and current axis positions. It
does not store a lock or owner.

The collision ranges, Placement Buffer Entry Z, and both handoff positions are
taught values. Placement does not block Supply while it is at or above Buffer
Entry Z, even when its X/Y position is still inside the Buffer area. Below that
Z, the one permitted overlap is the physical PCB handoff:

```text
Supply stops at its handoff position with the IPM fixer forward
  -> Placement opens its IPM gripper and lowers its IPM before entering
  -> Placement enters and stops at its handoff position
  -> Placement vacuum and IPM gripper inputs turn on
  -> Supply retracts the IPM fixer
  -> Supply retracts the Nest
  -> Supply moves down to Clear Z and exits in X
  -> Placement exits with the PCB assembly
```

While Placement enters, Supply must remain at its taught handoff position.
While Supply exits, Placement must remain at its taught handoff position. Any
other simultaneous overlap below Placement Buffer Entry Z is a Buffer conflict.

A handler is at Handoff only when every handler axis reports In Position, the
motion command has ended, and X/Y/Z are within 0.05 mm of the taught position.
Small stopped-position vibration therefore does not require exact coordinate
equality.

`PcbSupplier` and `PcbPlacer` do not read the Buffer PCB input directly. They
use the live entry, handoff, and exit conditions from this object. Neither
automatic unit references or calls the other.

Manual teaching is stricter than automatic handoff: a handler cannot be moved
manually while the other handler is inside the Buffer area.

At startup no Buffer state is restored from a file or memory. Homed axis
positions and live inputs are the only source of truth.

The Buffer PCB input may turn ON before Supply finishes its Handoff Z descent.
A stopped Supply already on that taught descent path may finish lowering while
Placement remains clear. Placement must still wait for Supply to settle at the
complete Handoff position. This does not permit a new entry into a filled Buffer.
