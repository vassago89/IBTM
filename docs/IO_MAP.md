# Confirmed IO

Source: `TMED2_IO_MAP_260901(S4T 변경사항 기재).xlsx`, including the user's corrections.
This is the existing machine configuration, not a request for additional IO.

## Corrections

- DI-146 does not exist. NG P3 and the shuttle carrier use the same DI-142.
- NG P1/P2 use DI-144/145. Shuttle down/up use DI-140/141.
- NG eject and eject-complete buttons use DI-149/14A. Emergency stop is separate.
- Both main and NG emergency stops inhibit all motion in Auto and Teaching.
- Auto door opening drops motion through the electrical circuit; Teaching permits
  motion with the doors open. Existing software cancellation observes device state.
- Both conveyors are IO driven. No conveyor alarm input or Start/Stop button is added.
- Ignore the escape sensors' "install later" note; keep DI-122/123 in the configured IO.
- Z axes are brake types. The fastening table cylinder remains excluded.
- Head 1 is pickup; Head 2 is shooting, regardless of the reversed AXIS IO labels.

## Changed addresses

| Unit / signal | DI | DO |
| --- | --- | --- |
| Supply rotation / unrotated | 104 / 105 | 104 / 105 |
| Supply gripper closed / open | 106 / 107 | 106 / 107 |
| Supply IPM fixer forward / backward | 108 / 109 | 108 / 109 |
| Main conveyor mode | 125 | — |
| Main conveyor entry / exit | 14B / 14C | — |
| NG transfer pickup down / up | 13B / 13C | 130 / 131 |
| NG transfer gripper closed / open | 13D / 13E | 132 / 133 |
| NG shuttle down / up | 140 / 141 | 134 / 135 |
| NG shuttle carrier / P3 | 142 | — |
| NG conveyor mode | 143 | — |
| NG conveyor P1 / P2 | 144 / 145 | — |
| NG stopper up / down | 147 / 148 | 136 / 137 |
| NG run / direction / normal speed | — | 138 / 139 / 13A |
| NG eject / eject complete button and lamp | 149 / 14A | 13B / 13C |

Supply 10A/10B are unused. The duplicated "forward/up" text in paired worksheet
rows does not add actuators; retain the established forward/backward and up/down pairs.
The conveyor mode inputs are available in IO diagnostics; they do not replace
the machine mode selector or create new automatic-run conditions.

## Saved settings

`IoMap260901` updates affected IO fields once when the machine database is opened.
It removes the old independent NG P3 input and renames Supply Nest to Supply Gripper.
Axis mapping, travel ranges, teaching positions, camera settings and recipes are retained.
Later IO edits are not overwritten on every startup. The source workbook is unchanged.

## NG behavior

The shuttle feedback owns P3 for the conveyor, transfer, UI and home checks.
A carrier remains detected through shuttle up/down motion. Virtual transfers it
onto the lower belt only when the shuttle is down; no second sensor is synthesized.
At capacity, the raised shuttle retains its carrier while P1 ejects and P2 compacts.
After operator confirmation, the retained carrier can be lowered and moved forward.
