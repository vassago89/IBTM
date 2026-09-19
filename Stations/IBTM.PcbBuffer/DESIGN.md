# PCB direct handoff

There is no physical buffer. `BufferStage` checks arrival at the two taught XYZ
poses and current holding/release inputs. There are no collision boundaries or
area entry/exit waits. The historical Buffer names preserve stored coordinates.

1. Supply rotates at Rotation Z, reaches give Z, then give XY.
2. Placement raises its handler, reaches receiving Z, then receiving XY. Either may arrive first.
3. When both are settled and Supply holds the PCB, Placement lowers its handler cylinder, confirms PCB detection, applies vacuum and closes the IPM gripper.
4. Supply confirms all three recipient signals before retracting its fixer and again before opening its gripper.
5. After Supply release, Placement raises its handler and may move directly to the selected heat sink. It does not wait for Supply withdrawal.
6. Supply waits for Placement Handler Up, returns XY at give Z to the next pickup, then reaches Rotation Z and unrotates.

Every Placement axis movement, including Z and HOME, requires Handler Up. The
machine stops Placement movement on loss of this feedback. Target arrival,
holding and release are still confirmed; relative handler positions are not used.
STOP retains no synthetic handoff step. Current feedback determines the next action.
