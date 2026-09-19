# PCB Placement Handler

Standby is receiving Z followed by receiving XY. `BufferHandoffPosition.Z` is
also the XY travel and rotation height. There is no separate wait coordinate.

1. Raise the handler, reach receiving Z, prepare the receiving orientation and IPM, then move to receiving XY.
2. Wait for Supply at its give XYZ with confirmed holding. Keep axes still, lower the handler, detect the PCB, apply vacuum and close the IPM gripper.
3. After Supply fixer and gripper retract, raise the handler. Both handlers may leave independently.
4. With the carrier seated, move directly to Heat Sink 1 XY, rotate, descend to placement Z and lower the handler.
5. Release vacuum, open the IPM gripper, raise IPM, close the gripper and lower IPM to press. Record the placement, then raise IPM, handler and Z.
6. Return to receiving XY for the second PCB and repeat at Heat Sink 2. Only detected heat sinks are targets; Heat Sink 2 requires no intermediate visit to Heat Sink 1.
7. Complete the carrier after the final placement is raised, then return to receiving standby.

Handler Up is required before and during every axis movement, including Z and
HOME. The handler cannot be commanded Down while an axis moves. Actual arrival,
seated-carrier and holding/release feedback remain in use. Supply area departure
and relative handler positions do not gate this sequence.

The press target belongs only to the current run because the Down inputs before
and after pressing are identical. STOP discards this history. Repeat uses the same
placement path with PCBs picked from the existing carrier, without Supply.
