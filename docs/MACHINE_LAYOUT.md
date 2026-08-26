# Machine Layout

This document defines the mechanical groups shown by the operator UI. It is based on
the component hierarchy read from the TMED2 EASM assembly.

## Source model

The EASM contains an HSF 19.10 model, material data, scene data, and a preview.
The loaded assembly has 18 root assemblies and 2,201 components. The control UI uses
the mechanical hierarchy below; covers, doors, frame members, fasteners, and cable
components are intentionally omitted.

| CAD root assembly | Components | Control responsibility |
| --- | ---: | --- |
| PCB PICKUP TRANSFER | 565 | Receives the upstream two-PCB carrier and presents each PCB to the buffer |
| PCB BUFFER JIG | 10 | Shared handoff and collision area between supply and placement |
| PCB HANDLER & PLACE | 335 | Picks from the buffer and places the PCB into a housing |
| BELT CONVEYOR | 251 | Main production carrier-jig conveyor and station backup plate hardware |
| Fastening assembly | 279 | One shared motion group carrying two fastening heads |
| NG TRANSFER | 102 | Shared Inspection Gantry and NG carrier pickup |
| NG CONVEYOR | 137 | Separate NG carrier transport; not the main production conveyor |
| NG SHUTTLE | 62 | Moves an NG carrier jig between NG handling positions |

## Operating layout

```text
Upstream two-PCB carrier
   Front 1 SMEMA
        v
PCB Supply Handler ---- PCB Buffer Jig ---- PCB Placement Handler
                                                   |
                                                   v
Front 2 SMEMA --> [PCB Placement] --> [Bolt Fastening] --> [Inspection] --> Rear SMEMA
                   main production carrier-jig conveyor
                                                     |
                                            Inspection Gantry
                                                     |
                                                NG Conveyor
                                                     |
                                                 NG Shuttle
```

The diagram describes ownership and material flow, not physical scale.

## PCB supply and buffer

The PCB Supply Handler model contains a long linear axis, two short linear-axis
assemblies, two rotary air-gripper assemblies, two PCB models, a pneumatic manifold,
and sensor hardware. The operator view therefore shows:

- the upstream carrier with PCB 1 and PCB 2;
- the supply handler position, rotation, PCB-present input, gripper input, and output;
- the supply-side SMEMA input and output;
- the PCB Buffer Jig as a compact shared handoff area.

PCB 1 and PCB 2 share one carrier Y and differ in X. The Buffer has the second
Supply Y. Supply moves in X/Y between those two lines, then leaves the Buffer at
Clear Z by moving X only while Buffer Y remains fixed.

The PCB Buffer Jig is not another motion station. Its UI state is PCB presence,
handler position, and collision conflict. Supply and placement motion remain responsible
for entering and leaving the configured buffer collision area.

## PCB placement

PCB Placement Handler owns the moving XYZ handler. It picks the PCB from the Buffer,
raises to Buffer Entry Z, moves above Housing 1, rotates, and waits. A confirmed carrier
jig and raised Backup Plate allow work only at detected housings. No placement-side
camera is controlled.

## Main carrier conveyor

`BELT CONVEYOR` is the only production conveyor shared by the three work stations.
It contains the working belt, width adjustment, end idlers, a drive motor, station
backup-plate hardware, and a housing carrier jig with two housing pockets.

`IBTM.PcbSupply` owns Front 1 SMEMA for the independent two-PCB carrier feed.
`IBTM.Conveyor` owns the shared housing Carrier Jig assembly, Front 2/Rear SMEMA,
stopper, and backup-plate mappings and commands. The Station projects retain their
live Carrier Jig, Housing, Backup Plate, and work-completion state. Station processes
do not reference or request `MainConveyor`; the conveyor observes their state.

A carrier jig on a raised backup plate is mechanically separated from the belt.
This allows the belt to move another carrier jig while a raised station continues
working. Stopper and backup-plate state is confirmed by digital inputs; output state
is displayed separately.

Each carrier jig has two housing slots. The two housing inputs at a station describe
which slots contain housings; they do not confirm that a carrier jig has arrived.
Every station uses the same slot mask: no housing skips work, housing 1 or 2 runs only
that position, and both housings run both positions. Station 1 may release the carrier
jig only after a PCB has been placed in every detected housing. An empty carrier jig
skips placement but is still distinguished from an empty station by its carrier-jig
arrival input.

The final I/O map assigns Carrier Jig arrival inputs to PCB Placement (`DI-128`),
Bolt Fastening (`DI-12F`), and Inspection (`DI-136`). Their housing sensors remain
only the work-slot mask.

Automatic transfer selection must be ordered
from rear to front: Inspection to Rear, Bolt Fastening to Inspection, PCB Placement
to Bolt Fastening, and Front to PCB Placement. A blocked rear transfer must not block
a runnable transfer in front of it, and a started transfer must run to its destination
without being preempted. All four routes use this selection order. `ConveyorState`
is derived again from live inputs after each relevant I/O or work-state change; it is
not a remembered sequence step.

An OK Carrier Jig completed at Inspection turns on `Available To Rear` and remains
seated while `Ready From Rear` is off. Once rear ready is on, the Inspection backup
plate and stopper go down before the conveyor starts. The conveyor stops when the
Inspection Carrier Jig input turns off, then `Available To Rear` turns off.

Placement, Bolt Fastening, and Inspection report work complete only after every
detected housing finishes. A disabled station process is bypassed by the WPF host so
Main Conveyor can be validated independently. Inspection moves the camera to every
recipe Bolt Point belonging to a detected housing and records bolt presence without
stopping at the first missing bolt.
Station work starts only after Carrier Jig, Backup Plate Up, and Stopper Down inputs
all confirm the final seated state.

Only the backup plates participating in a future transfer are lowered. A source
Stopper is lowered to release its Carrier Jig; a destination Stopper is raised before
the belt starts so the arriving Carrier Jig is physically stopped. The destination
Carrier input stops the belt, then the Backup Plate rises and the Stopper returns down.
Stop stops the belt and leaves pneumatic outputs at their current state. If the
source Carrier input remains on, its completed work state remains valid and the
same transfer can start again. A Carrier stopped between station sensors still
requires manual recovery because no input identifies its position.

Front Available is the receive trigger. After it arrives, PCB Placement raises its
Stopper and lowers its Backup Plate before Front Ready is asserted and both conveyors
run. Rear Available is advertised after Inspection work completes and Rear Ready is
the discharge trigger. Rear wins when it is ready; a blocked Rear does not prevent an
already-waiting Front carrier from entering. Outputs are commands, not state evidence.

The machine has three external SMEMA connections. Front 1 belongs to PCB Supply,
Front 2 belongs to the main housing carrier-jig conveyor, and Rear belongs to the
main conveyor exit.

| Connection | Input | Output |
| --- | --- | --- |
| Front 1 (PCB) | `DI-100` Available From Front 1 | `DO-100` Ready To Front 1 |
| Front 2 (Housing) | `DI-101` Available From Front 2 | `DO-101` Ready To Front 2 |
| Rear | `DI-102` Ready From Rear | `DO-102` Available To Rear |

## Bolt fastening

The fastening assembly is one mechanical motion group. Head 1 is the pickup
head; Head 2 is the shooting head. They are mounted together and have separate ADC
tightening controllers on one RS-422 bus. The pickup head uses its own vacuum pump and vacuum sensor
to pick a screw from the fixed ZEDA KS1069C-S pickup index. Feeder control and
feedback wiring remain pending. Slave address 1 belongs to the shooting head and
slave address 2 belongs to the pickup head by default; those addresses are not the
mechanical head numbers. The operator view shows one moving gantry body with
two heads, not two independent motion systems.

Bolt X/Y positions are taught with the Station 3 camera and stored in carrier-relative
millimetres. The upper-left and lower-right carrier locating pins define the carrier
origin and angle. Station 3 teaches both pins with the camera centre; Station 2 teaches
the same two pins separately for each fastening head. The Station 3-to-Station 2
machine-coordinate conversion applies only translation and rotation because all axes
already report physical X/Y; it never stretches the bolt pattern. Each bolt point identifies Housing 1 or
Housing 2 so the station can use its housing-present inputs as the work mask.

The Station 3 teaching screen scans between taught upper-left and lower-right gantry
positions in a snake path using machine-coordinate X/Y pitches. The scan path does
not depend on the millimetres-per-pixel value that is adjusted afterward. Every
original camera frame is stored in the recipe's
`Carrier` directory with its capture-centre XY in `Recipe.json`. WPF draws the frames
at those machine coordinates; no stitched bitmap is created. The operator adjusts
millimetres per pixel until overlapping frames align, then clicks the carrier locating
pins and bolt points directly on the map. Clicks therefore produce Station 3 machine
XY immediately, and bolt points remain stored in carrier-relative millimetres.

Commission the image map in this order:

1. Mount the camera so image X/Y matches the machine X/Y directions.
2. Scan once and confirm the X/Y pitches leave visible overlap.
3. Adjust millimetres per pixel until features in the overlap coincide.
4. Click the upper-left and lower-right locating pins. Untaught pins and bolts are not
   drawn as markers.
5. Teach one bolt in Station 3, teach the matching head references in Station 2, and
   verify that transformed point before adding the remaining bolts.

The Station 2 automatic process is armed by the `Bolt Fastening Backup Plate Up` input.
It reads the housing-present input for each recipe point and fastens the points whose
`HousingSlot` is present, in bolt-number order. With neither housing present it
performs no bolt work. The backup plate must go down before another carrier cycle is
accepted. A tightening NG or pneumatic feedback timeout raises the Bolt Fastening
alarm. Screw feeding, bolt pickup, and dispensing remain outside this process until
their final mechanical sequence and feedback wiring are confirmed.

Both fastening-head up inputs are confirmed before the shared gantry moves. This also
resolves a head left down by Stop before a restarted fastening cycle can move X/Y.

## Inspection and NG handling

The inspection optics are nested inside the CAD assembly named `NG TRANSFER / NG XY GANTRY`.
In control code, its shared X/Y motion is named `InspectionGantry`. The assembly
contains the inspection camera, lens, light, servo axes, linear stages, and pneumatic
slides. The inspection camera and NG carrier gripper therefore move together in the
operator view; there is no independent Inspection Handler or NG Transfer motion group.

The controlled camera assembly is:

| Role | Camera | Lens | Light |
| --- | --- | --- | --- |
| Inspection | MV-CU013-A0GMGC | MVL-MF2518M | LAB-IDL100 |

`IBTM.Inspection` owns the Inspection Gantry axes and its NG carrier gripper.
`IBTM.NgConveyor` owns NG Shuttle and NG Conveyor hardware and observes the
Inspection work result through a one-way project reference. Inspection never requests
NG transfer; the NG Conveyor loop pulls a completed NG Carrier Jig when capacity is
available.

Station 3 uses two machine teaching positions for the carrier scan bounds. Scan
overlap, locating-pin, NG pickup, and NG shuttle placement coordinates are machine
settings. The tile centres, millimetres per pixel, and original images belong to the
recipe image set.

Automatic inspection transforms the same carrier-relative Bolt Points through the
Station 3 locating pins. The camera centres each point and crops the configured square
ROI. A Tiny U-Net model segments the bolt recess, then the ratio of mask pixels above
the mask threshold determines bolt presence. Results are retained by housing and bolt
number. A missing bolt makes the carrier NG only after all remaining bolt points have
also been inspected. A Carrier Jig with neither housing present skips image capture
and is completed as NG.

NG Conveyor and NG Shuttle are separate mechanical assemblies below Station 3. The
three NG Conveyor position sensors are `DI-140`, `DI-141`, and `DI-142`. They are
shown as a secondary handling path next to the inspection transfer. NG capacity
counts carrier jigs, not individual PCBs. The Shuttle lowers a new Carrier Jig at
Position 3. With Position 1 empty the belt moves it to Position 1; otherwise it moves
to Position 2, and with Positions 1 and 2 occupied it remains at Position 3. All three
occupied inputs block the next NG pickup at Station 3. The eject button releases the
Carrier Jig at Position 1 and the remaining jigs move forward. The complete lamp then
stays on until the operator removes the ejected jig and presses the eject-complete
button; operator confirmation has no automatic timeout.

## UI rules

- Show the machine as one continuous piece of equipment.
- Show the machine state and the next operator action at the top of the screen.
- Read the material flow from left to right as `PCB Supply -> Buffer -> Station 1
  Place -> Station 2 Fasten -> Station 3 Inspect -> Exit / NG`.
- Keep the physical station names `Station 1`, `Station 2`, and `Station 3`.
  Do not renumber the supply and buffer as extra stations.
- Treat Supply, Buffer, and Placement as one visible handoff flow while
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
- Show the inspection camera and NG gripper on the moving Inspection Gantry.
- Show exactly one main carrier conveyor.
- Show NG Conveyor and NG Shuttle as a secondary path, not as another production
  station or a generic NG Stack box.
- Show digital inputs as round indicators and outputs as square indicators. Label
  mechanism pairs such as `Grip`, `Stopper`, and `Plate`; tooltips are supplementary.
- Use input state for mechanism confirmation. Output state only shows the command.
- Derive operator states from digital inputs and live motion state. Never infer
  mechanism completion from an output command.
- Apply the same rule in Virtual: actuator feedback changes after the fixed virtual
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
