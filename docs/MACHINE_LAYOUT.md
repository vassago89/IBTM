# Machine Layout

This document defines the mechanical groups shown by the operator UI. It is based on
the component hierarchy read from `TMED2-00-00_조립_260712.easm`.

## Source model

The EASM contains an HSF 19.10 model, material data, scene data, and a preview.
The loaded assembly has 18 root assemblies and 2,201 components. The control UI uses
the mechanical hierarchy below; covers, doors, frame members, fasteners, and cable
components are intentionally omitted.

| CAD root assembly | Components | Control responsibility |
| --- | ---: | --- |
| PCB PICKUP TRANSFER | 565 | Receives the upstream two-PCB carrier and presents each PCB to the buffer |
| PCB BUFFER JIG | 10 | Shared handoff and collision area between supply and placement |
| PCB HANDLER & PLACE | 335 | Picks from the buffer, captures fiducials, and places the PCB into a housing |
| ALIGN optical assembly | 10 | Fixed upward-looking alignment camera |
| BELT CONVEYOR | 251 | Main production carrier-jig conveyor and station backup plate hardware |
| Fastening assembly | 279 | One shared motion group carrying two fastening heads |
| NG TRANSFER | 102 | Inspection and NG XY transfer |
| NG CONVEYOR | 137 | Separate NG carrier transport; not the main production conveyor |
| NG SHUTTLE | 62 | Moves an NG carrier jig between NG handling positions |

## Operating layout

```text
Upstream two-PCB carrier
        |
        v
PCB Pickup Transfer ---- PCB Buffer Jig ---- PCB Handler & Place
                                                   |
                                         fixed Alignment Camera
                                                   |
                                                   v
SMEMA IN --> [PCB Placement] --> [Bolt Fastening] --> [Inspection] --> SMEMA OUT
                   main production carrier-jig conveyor
                                                     |
                                              NG XY Transfer
                                                     |
                                                NG Conveyor
                                                     |
                                                 NG Shuttle
```

The diagram describes ownership and material flow, not physical scale.

## PCB supply and buffer

The PCB Pickup Transfer model contains a long linear axis, two short linear-axis
assemblies, two rotary air-gripper assemblies, two PCB models, a pneumatic manifold,
and sensor hardware. The operator view therefore shows:

- the upstream carrier with PCB 1 and PCB 2;
- the supply handler position, rotation, PCB-present input, gripper input, and output;
- the supply-side SMEMA input and output;
- the PCB Buffer Jig as a compact shared handoff area.

The PCB Buffer Jig is not another motion station. Its UI state is PCB presence,
current owner, and collision conflict. Supply and placement motion remain responsible
for entering and leaving the configured buffer collision area.

## PCB placement and alignment

PCB Handler & Place owns the moving XYZ handler. The Alignment Camera is a separate,
fixed optical assembly below the PCB path. Alignment is performed by moving the PCB
over the fixed camera for two fiducial captures.

The operator view must not draw the alignment camera as part of the moving handler.
Only the handler position changes in real time.

## Main carrier conveyor

`BELT CONVEYOR` is the only production conveyor shared by the three work stations.
It contains the working belt, width adjustment, end idlers, a servo motor, station
backup-plate hardware, and a housing carrier jig with two housing pockets.

A carrier jig on a raised backup plate is mechanically separated from the belt.
This allows the belt to move another carrier jig while a raised station continues
working. Stopper and backup-plate state is confirmed by digital inputs; output state
is displayed separately.

## Bolt fastening

The fastening assembly is one mechanical motion group. Head 1 is the standard head;
Head 2 is the Loctite head. They are mounted together and have separate ADC-400
tightening controllers. The Loctite head uses its own vacuum pump and vacuum sensor
to pick a screw from the fixed ZEDA KS1069C-S pickup index. Feeder control and
feedback wiring remain pending. The operator view shows one moving gantry body with
two heads, not two independent motion systems.

## Inspection and NG handling

The inspection optics are nested inside `NG TRANSFER / NG XY GANTRY`. The assembly
contains the inspection camera, lens, light, servo axes, linear stages, and pneumatic
slides. The inspection camera must therefore move with the NG transfer in the
operator view; there is no independent Inspection Handler.

The camera assemblies are:

| Role | Camera | Lens | Light |
| --- | --- | --- | --- |
| Alignment | MV-CU013-A0GMGC | MVL-MF2518M | LAB-IDL100 |
| Inspection | MV-CU013-A0GMGC | MVL-MF2518M | LAB-IDL100 |

NG Conveyor and NG Shuttle are separate mechanical assemblies. They are shown as a
secondary handling path next to the inspection transfer. NG capacity counts carrier
jigs, not individual PCBs.

## UI rules

- Show the machine as one continuous piece of equipment.
- Show the machine state and the next operator action at the top of the screen.
- Read the material flow from left to right as `PCB Supply -> Buffer -> Station 1
  Place -> Station 2 Fasten -> Station 3 Inspect -> Exit / NG`.
- Keep the physical station names `Station 1`, `Station 2`, and `Station 3`.
  Do not renumber the supply and buffer as extra stations.
- Treat Supply, Buffer, Alignment, and Placement as one visible handoff flow while
  retaining their separate control responsibilities.
- Distinguish `Home Required`, `Servo Off`, `Motion Fault`, safety interlocks,
  normal readiness, running, and NG capacity. Do not collapse them into one generic
  stopped state.
- Show moving handlers at live axis positions; do not draw axis blocks as the main
  objects.
- Keep machine structure subdued. Use green for PCB material, amber for housings,
  blue for carrier jigs and active mechanisms, and red only for alarms that require
  action.
- Do not show PCB 1 or PCB 2 as present from SMEMA alone. Their presence remains
  unknown until the supply handler's PCB sensor checks each pickup position.
- Show the fixed alignment camera separately from the placement handler.
- Show the inspection camera inside the moving NG transfer.
- Show exactly one main carrier conveyor.
- Show NG Conveyor and NG Shuttle as a secondary path, not as another production
  station or a generic NG Stack box.
- Show digital inputs as round indicators and outputs as square indicators. Label
  mechanism pairs such as `Grip`, `Stopper`, and `Plate`; tooltips are supplementary.
- Use input state for mechanism confirmation. Output state only shows the command.
- Derive operator states from digital inputs and live motion state. Never infer
  mechanism completion from an output command.
- Apply the same rule in Virtual: actuator feedback changes after its configured
  delay, then the simulated PCB or carrier-jig position changes.
- Keep axis coordinates as secondary diagnostic information.
- Keep covers and frame geometry subdued so material position and mechanism state
  remain readable.

## Remaining mechanical confirmation

The EASM component tree identifies ownership accurately, but the reader currently
does not expose component transforms or bounding boxes. Exact screen coordinates are
therefore an operator-oriented schematic rather than a CAD projection. Axis
directions, travel limits, and physical channel numbers remain editable machine
settings and must be verified during commissioning.
