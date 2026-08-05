# PCB Buffer Stage

`BufferStage` owns exclusive access to the physical PCB buffer shared by the
supply and placement handlers.

## Dependency direction

```text
IBTM.PcbBuffer
IBTM.PcbSupply
IBTM.Stations.PcbPlacement
        ↑
IBTM host coordinates all three
```

Supply and placement reference neither each other nor `IBTM.PcbBuffer`.
`PcbBufferService` in the WPF host owns the complete shared-buffer transaction
and passes the Buffer cancellation token into the selected handler operation.

## Ownership

```csharp
public enum BufferOwner
{
    None,
    Supply,
    Placement,
}
```

`PcbBufferService` calls `EnterAsync`, runs the selected handler, verifies its
clear position, and then calls `ExitSupply` or `ExitPlacement`. Handlers own
only their motion and IO steps.

The internal `SemaphoreSlim` provides mutual exclusion only. FIFO ordering is
not required: an empty buffer permits Supply work, while an occupied buffer
permits Placement work. The physical buffer condition will select the eligible
handler after its inputs are confirmed.

## Collision area

The mechanical collision area belongs to machine settings, not the recipe.
Supply uses an X range because it has no Y axis. Placement uses an XY rectangle.

```text
Supply X minimum / maximum
Placement X minimum / maximum
Placement Y minimum / maximum
```

The ranges must be configured before either handler can enter the buffer.
There is no bypass switch for this mechanical interlock.

`CanEnter` controls command availability. `EnterAsync` enforces the same collision
area at the execution boundary, so a caller cannot bypass the mechanical interlock.
Normal buffer operations acquire ownership before moving into the area.
`ExitSupply` and `ExitPlacement` release ownership only after the current motion
position is outside the corresponding area.

## Supply access

```text
Enter as Supply
  -> move to Supply Buffer position
  -> place PCB
  -> open Supply gripper
  -> Safe Z
  -> X origin outside the machine
  -> unrotate
  -> Exit
```

`PcbBufferService` owns the transaction. `PcbSupplyHandler` performs only the
motion and IO steps using the token supplied by the coordinator.

## Placement access

```text
Enter as Placement
  -> move to Placement Buffer position
  -> close Placement gripper
  -> move to the supplied clear position
  -> Exit
```

`PcbBufferService` owns the transaction. `PcbPlacementStation` performs only
the motion and IO steps using the supplied token. The intended clear position
is Fiducial 1 unless the mechanical layout later requires a separate taught
position.

## Stop and cancellation

Supply, Placement, and Buffer do not own separate cancellation tokens.
`BufferStage` owns one cancellation source for the complete buffer operation.
`EnterAsync` returns that shared token to `PcbBufferService`, which passes it
through every handler motion and IO wait until the handler exits.

```text
one Buffer cancellation source
  -> BufferStage.EnterAsync
  -> Supply motion and IO
  -> Placement motion and IO
```

Stop and Emergency Stop cancel this one source, so the current owner and the
other handler waiting to enter are canceled together. When no handler owns the
buffer, cancellation immediately creates the token for the next operation.

Buffer ownership is not released by cancellation, Stop, Emergency Stop, or the
normal machine Reset. Any operation that did not reach its verified `Exit`
leaves the Buffer canceled and owned. The process screen displays
`Recovery Required`; recovery moves the owner to its clear position. A verified
`Exit` then releases ownership and creates the next cancellation token.

## Not defined yet

- Buffer clearance inputs
- Buffer clamp, lift, or other pneumatic actuators
- Startup recovery when no in-memory owner exists but a handler is already
  inside the buffer
