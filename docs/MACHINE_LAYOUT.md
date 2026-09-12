# Machine Layout

> 기구 배치 참고 문서다. 아래의 과거 IO 번호·동작 설명을 현재 제어 사양으로 사용하지 않는다.
> 현재 주소/극성은 실행 중 Settings와 [IO 안내](IO_MAP.md),
> 운전·Repeat는 [현장 확인 안내](STATION3_COMMISSIONING.md)를 먼저 확인한다.

This document defines the mechanical groups shown by the operator UI. It is based on
the component hierarchy read from the TMED2 EASM assembly.

## Source model

The EASM contains an HSF 19.10 model, material data, scene data, and a preview.
The saved model payload and preview are under `artifacts/cad/extracted`; the
original exported EASM and HTML are in the user's Documents folder. Reuse these
exports and the established layout below for UI work. The small preview shows the
machine enclosure, not a dimensioned internal plan, so do not infer PCB dimensions
from it.
The loaded assembly has 18 root assemblies and 2,201 components. The control UI uses
the mechanical hierarchy below; covers, doors, frame members, fasteners, and cable
components are intentionally omitted.

| CAD root assembly | Components | Control responsibility |
| --- | ---: | --- |
| PCB PICKUP TRANSFER | 565 | Receives the upstream two-PCB carrier and presents each PCB to the buffer |
| PCB BUFFER | 10 | Shared handoff and collision area between supply and placement |
| PCB HANDLER & PLACE | 335 | Picks from the buffer and places the PCB into a heat sink |
| BELT CONVEYOR | 251 | Main production carrier conveyor and station backup plate hardware |
| Fastening assembly | 279 | One shared motion group carrying two fastening heads |
| NG TRANSFER | 102 | Shared Inspection Gantry and NG carrier pickup |
| NG CONVEYOR | 137 | Separate NG carrier transport; not the main production conveyor |
| NG SHUTTLE | 62 | Moves an NG carrier between NG handling positions |

## Operating layout

```text
Upstream two-PCB carrier
   Front 1 SMEMA
        v
PCB Supply Handler ---- PCB Buffer ---- PCB Placement Handler
                                                   |
                                                   v
Front 2 SMEMA --> [PCB Placement] --> [Bolt Fastening] --> [Inspection] --> Rear SMEMA
                   main production carrier conveyor
                                                                 |
                                                        NG Carrier Transfer
                                                                 |
                                                                 v
                                                            NG Shuttle
                                                                 |
                                                                 v
                                                            NG Conveyor
```

The diagram describes ownership and material flow, not physical scale.

All nine automatic execution units share the `AutoUnit` base: Main Conveyor,
PCB Supply, PCB Placement, Pickup Bolt Feeder, Shooting Bolt Feeder, Bolt Fastening,
Inspection / NG Carrier Transfer, NG Shuttle and NG Conveyor. Inspection and
NG Carrier Transfer use one loop because they share the same gantry.

Actions run sequentially; passive states wait for relevant change notifications.
State enums, IO/motion commands, device Stop and cleanup remain unit-owned. Supply
keeps its PCB 1/2 selection local to each run. Feeders retain their own supply
timeouts, and fastening/inspection retain their per-carrier cancellation scopes.
The buffer's one-shot condition waits and hardware communication/monitoring loops
are not automatic units and do not inherit this base.

## PCB supply and buffer

The PCB Supply Handler model contains a long linear axis, two short linear-axis
assemblies, two rotary air-gripper assemblies, two PCB models, a pneumatic manifold,
and sensor hardware. The operator view therefore shows:

- the upstream carrier with PCB 1 and PCB 2;
- the supply handler position, rotation, PCB-present input, gripper input, and output;
- the supply-side SMEMA input and output;
- the PCB Buffer as a compact shared handoff area.

PCB 1 and PCB 2 share one carrier Y and differ in X. The Buffer has the second
Supply Y. Supply moves in X/Y between those two lines, then leaves the Buffer at
Clear Z by moving X only while Buffer Y remains fixed.

The PCB Buffer is not another motion station. Its UI state is PCB presence,
handler position, and collision conflict. Supply and placement motion remain responsible
for entering and leaving the configured buffer collision area.

## PCB placement

PCB Placement Handler owns the moving XYZ handler. It picks the PCB from the Buffer,
keeps the IPM Down, raises the Handler and Z to Buffer Entry Z, moves above Heat Sink 1,
rotates, and waits. A confirmed carrier
and raised Backup Plate allow work only at detected heat sinks. No placement-side
camera is controlled. At a target heat sink it lowers the Handler, releases vacuum,
opens the IPM gripper, raises the IPM, closes the gripper, lowers the IPM to press, records that
heat sink, then raises the IPM, Handler, and Z. The carrier is completed only after
this final raised state for every detected heat sink.

Pressing requires the taught placement XYZ, rotated feedback, and Handler Down.
The pending press target survives Stop because the initial carrying Down and the
completed pressing Down have identical feedback. Restart during Close or Down
continues that press; it does not reopen the gripper or raise the IPM again.

## Main carrier conveyor

`BELT CONVEYOR` is the only production conveyor shared by the three work stations.
It contains the working belt, width adjustment, end idlers, a drive motor, station
backup-plate hardware, and a heat sink carrier with two heat sink pockets.

`IBTM.PcbSupply` owns Front 1 SMEMA for the independent two-PCB carrier feed.
`IBTM.Conveyor` owns the shared heat-sink carrier conveyor, Front 2/Rear SMEMA,
stopper, and backup-plate mappings and commands. The Station projects retain their
live Carrier, Heat Sink, Backup Plate, and work-completion state. Station processes
do not reference or request `MainConveyor`; the conveyor observes their state.

A carrier on a raised backup plate is mechanically separated from the belt.
This allows the belt to move another carrier while a raised station continues
working. Stopper and backup-plate state is confirmed by digital inputs; output state
is displayed separately.

Each carrier has two heat sink slots. The two heat sink inputs at a station describe
which slots contain heat sinks; they do not confirm that a carrier has arrived.
Every station uses the same slot mask: no heat sink skips work, heat sink 1 or 2 runs only
that position, and both heat sinks run both positions. Station 1 may release the carrier
carrier only after a PCB has been placed in every detected heat sink. An empty carrier
skips placement but is still distinguished from an empty station by its carrier
arrival input.

The final I/O map assigns Carrier arrival inputs to PCB Placement (`DI-128`),
Bolt Fastening (`DI-12F`), and Inspection (`DI-136`). The main conveyor entry and
exit sensors are `DI-14B` and `DI-14C`. Their heat sink sensors remain only the
work-slot mask.

Automatic transfer selection must be ordered
from rear to front: Inspection to Rear, Bolt Fastening to Inspection, PCB Placement
to Bolt Fastening, and Front to PCB Placement. A blocked rear transfer must not block
a runnable transfer in front of it, and a started transfer must run to its destination
without being preempted. An active entry or exit boundary sensor is an already-started
transfer and is resumed before selecting a new station transfer. All four routes use
this selection order. `MainConveyorState`
is derived again from live inputs after each relevant I/O or work-state change; it is
not a remembered sequence step.

An OK Carrier completed at Inspection turns on `Available To Rear` and remains
seated while `Ready From Rear` is off. Once rear ready is on, the Inspection backup
plate and stopper go down before the conveyor starts. The carrier passes the exit
sensor from ON to OFF before the conveyor stops and `Available To Rear` turns off.

Placement, Bolt Fastening, and Inspection report work complete only after every
detected heat sink finishes. A disabled station process is bypassed by the WPF host so
Main Conveyor can be validated independently. Inspection moves the camera to every
recipe Bolt Point belonging to a detected heat sink and records bolt presence without
stopping at the first missing bolt.
Station work starts only after Carrier, Backup Plate Up, and Stopper Down inputs
all confirm the final seated state.

Only the backup plates participating in a future transfer are lowered. A source
Stopper is lowered to release its Carrier; a destination Stopper is raised before
the belt starts so the arriving Carrier is physically stopped. The destination
Carrier input stops the belt, then the Backup Plate rises and the Stopper returns down.
Stop stops the belt and leaves pneumatic outputs at their current state. If the
source Carrier input remains on, its completed work state remains valid and the
same transfer can start again. When the destination Carrier input turns on, the
previous station's process results move with it. The departed station retains those
results only until its next Carrier arrives. A Carrier stopped between station
sensors still requires manual recovery because no input identifies its position.

Front Available is the receive trigger. After it arrives, PCB Placement raises its
Stopper and lowers its Backup Plate before Front Ready is asserted and both conveyors
run. Front Ready turns off when the entry sensor turns on. The conveyor continues
until the PCB Placement Carrier input turns on, then seats the carrier. Rear
Available is advertised after Inspection work completes and Rear Ready is the
discharge trigger. Rear wins when it is ready; a blocked Rear does not prevent an
already-waiting Front carrier from entering. Front Ready and Rear Available are not
advertised together. Outputs are commands, not state evidence.

After Stop, an active entry sensor resumes the unfinished receive and an active exit
sensor resumes the unfinished discharge. No transfer phase is persisted. A carrier
between two point sensors with both inputs off remains intentionally unknown and
requires manual recovery.

The machine has three external SMEMA connections. Front 1 belongs to PCB Supply,
Front 2 belongs to the main heat sink carrier conveyor, and Rear belongs to the
main conveyor exit.

| Connection | Input | Output |
| --- | --- | --- |
| Front 1 (PCB) | `DI-100` Available From Front 1 | `DO-100` Ready To Front 1 |
| Front 2 (Heat Sink) | `DI-101` Available From Front 2 | `DO-101` Ready To Front 2 |
| Rear | `DI-102` Ready From Rear | `DO-102` Available To Rear |

## Bolt fastening

The fastening assembly is one mechanical motion group. Head 1 is the pickup
head; Head 2 is the shooting head. They are mounted together and have separate ADC
tightening controllers on one RS-422 bus. The pickup head uses its own vacuum pump
and vacuum sensor to pick a screw from the fixed ZEDA KS1069C-S pickup index.
Slave address 1 belongs to the pickup head and slave address 2 belongs to the
shooting head by default; those addresses are independent from mechanical head
numbering. The operator view shows one moving gantry body with two heads, not two
independent motion systems.

Bolt X/Y positions are taught with the Station 3 camera and stored in carrier-relative
millimetres. The upper-left locating pin is (0, 0), and the Station 3 image X/Y
directions are the carrier X/Y directions. The lower-right locating pin supplies the
reference vector used to align Station 2. Station 3 teaches both pins on the carrier
image; Station 2 teaches the same two pins separately for each fastening head. The
Station 3-to-Station 2 conversion applies only translation and rotation because all
axes already report physical X/Y; it never stretches the bolt pattern. Each bolt
point identifies Heat Sink 1 or Heat Sink 2 so the station can use its heat sink-present
inputs as the work mask.

The Station 3 teaching screen scans between taught upper-left and lower-right gantry
positions in a snake path. The step is calculated from the physical camera FOV minus
one overlap setting, then distributed so the final taught position is included. The
scan path does not depend on the millimetres-per-pixel value that is adjusted
afterward. Every original camera frame is stored in the recipe's
`Carrier` directory with its capture-centre XY in `Recipe.json`. WPF draws the frames
at those machine coordinates; no stitched bitmap is created. The operator adjusts
millimetres per pixel until overlapping frames align, then clicks the carrier locating
pins and bolt points directly on the map. Clicks therefore produce Station 3 machine
XY immediately, and bolt points remain stored in carrier-relative millimetres.
The last successfully saved or loaded recipe is restored at startup. Creating a new
unsaved recipe does not replace that active selection.

Commission the image map in this order:

1. Mount the camera so image X/Y matches the machine X/Y directions.
2. Scan once and confirm the X/Y pitches leave visible overlap.
3. Adjust millimetres per pixel until features in the overlap coincide.
4. Click the upper-left and lower-right locating pins. Before both pins are taught,
   the image map does not display machine coordinates. Afterward it displays only
   upper-left-relative carrier X/Y. Each locating pin can be re-taught independently;
   re-teaching the upper-left pin preserves the existing lower-right pin. The same
   rule applies to both Station 2 fastening heads. Untaught pins and bolts are not
   drawn as markers.
5. Teach one bolt in Station 3, teach the matching head references in Station 2, and
   verify that transformed point before adding the remaining bolts.

Set millimetres per pixel before teaching the locating pins and bolts. If that value
was wrong when the points were taught, teach those points again after correcting it.
Station 2 and Station 3 must also use the same logical X/Y handedness through their
axis-direction settings. A single mirrored axis is a reflection and cannot be derived
from the two locating pins as a rotation.

Automatic start is blocked until the recipe contains at least one bolt point, the
shared carrier pins and every bolt X/Y are taught, and, when Bolt Fastening is
enabled, the locating pins for each used fastening head are taught. Both heads
fasten at the shared, machine-taught Safe Z; there is no per-bolt Z teaching.

Station 2 starts incomplete work with the carrier present, backup plate raised and
stopper lowered. It selects the currently detected heat sinks at the start of each
run, then performs all Head 2 PCB bolts, all Head 1 IPM seating bolts, and all Head 1
IPM final passes, ordered by heat sink and bolt number. With neither heat sink
present it performs no bolt work. Torque NG is recorded without skipping remaining
bolts; communication failures and pneumatic timeouts stop operation. Carrier
arrival resets the work, while restart reselects the currently detected heat sinks.
Bolt feeding runs independently; pickup, shooting and tightening belong to the
fastening process.

If Stop interrupts ADC result reception, the head first sends Stop and then reads
the latest result once. Only a completion with a new event count is returned to
the original bolt/pass call; an unfinished operation remains cancelled. No new
Start is issued during this read, and the station checks cancellation before its
next action. Stop cleanup can therefore wait for one extra ADC response, bounded
by the communication response timeout; cancellation itself is not postponed.
Completed results stay on the original assembly even if the carrier changes.

Both fastening-head Up inputs must be ON, and their Down inputs OFF,
before automatic or saved-position shared-gantry X/Y moves and horizontal Home.
The selected head lowers only at the target.
All PCB, IPM seating and IPM final fastening positions use that same Safe Z.
Travel between bolts is X/Y only with both head cylinders raised; the selected
head cylinder provides the fastening stroke. Common-Z travel is needed for IPM
bolt pickup and return to Safe Z, not for approaching each fastening point.
For the shooting head, loading happens while the head remains raised: wait for
head-vacuum bolt detection, tube passage to clear and escape retraction, then lower
the head and tighten. After Stop, these same inputs determine the next action;
a lowered head without a detected bolt is raised before loading again.
Escape advance and shooting are separate states. With the escape backward, wait
for feeder-ready before advancing; an intermediate position finishes advancing.
Forward feedback selects shooting directly, without waiting for the next feeder
bolt or retracting and advancing again. The vacuum is turned ON before shooting
air. Tube-passage monitoring is armed before air ON to capture short pulses.
Stop retains the escape output, so forward feedback may arrive while stopped;
restart uses that feedback rather than replaying the loading sequence.
Moving to a bolt position does not change the escape output. Tube detection
blocks that X/Y move until the tube clears, including after manual repositioning.
This applies between individual bolts, including the shooting head's PCB pass;
the shooting head no longer stays lowered while moving to the next bolt. On restart,
the same live-DI states retract a lowered head before the next X/Y move.

The pickup head approaches pickup XY at Safe Z with both heads raised. Head 1
then lowers and confirms its Down input before the common Z axis moves to the
taught pickup height. After feeder-ready detection, vacuum turns ON and its input
confirms the picked bolt. Retreat keeps vacuum ON: common Z to Safe Z, Head 1 Up,
then X/Y to the fastening point. Pickup Z is therefore taught with Head 1 lowered.
Pickup approach, cylinder lowering, Z approach, feeder wait, vacuum pickup and
retreat are separate live-feedback states. Clearing a completed fastening turns
vacuum OFF; carrying a newly picked bolt does not. Every IPM seating bolt is picked
anew, while the IPM final pass reuses the installed bolts.

Feeder-wait states wake on relevant work, gantry and feeder feedback changes and
reselect the next state. A delayed vacuum or escape input must not leave the
station waiting for another feeder bolt. These notifications coalesce into one
wake-up; they do not start parallel actions. Feeders retain their own supply
timeouts. Active motion and pneumatic actions still await their own completion.

The Bolt Pickup teaching point's Move To uses the same gantry-owned pickup XY,
Head 1 Down confirmation and pickup-Z actions as automatic operation. It finishes
with Head 1 down at the pickup position without changing either vacuum output or
waiting for a feeder bolt. Stop cancels any remaining action and keeps pneumatic
outputs; ordinary saved-position admission still requires both heads raised.
Return from Pickup is available throughout Bolt Fastening teaching, independently
of the selected point: move Z to Safe Z, then raise Head 1 and confirm its Up input.
It does not move X/Y or change vacuum. Stop, screen/group changes and Manual-mode
exit cancel the remaining steps. Manual cylinder timeouts, whether from a movement
sequence or an IO button, report the owning unit's alarm through the common
teaching execution boundary; backup-plate timeouts remain Main Conveyor alarms.

Tube detection ON with no head-vacuum detection requires operator clearing;
automatic operation waits for the tube to clear without feeding another bolt.
Station 2 teaching exposes a momentary HOLD button beside Shoot Bolt. It controls
only shooting air, not the feeder, escape, head cylinder or vacuum. Release,
navigation, Stop, shutdown or leaving Manual mode cancels the hold and turns the
air OFF. Existing manual-output readiness applies; Home is not required.

Bolt teaching Jog/Step are manual single-axis adjustments at the current Z, with
head-down adjustment allowed. They do not run the Safe-Z positioning sequence;
saved-position commands still require raised heads. Axis limits, cancellation and
manual motion/safety readiness remain in force. A software axis range is not a
verified mechanical clearance envelope; commissioning must establish suitable
adjustment speeds and collision-free travel around the workpiece.
The pickup-feeder loop waits for its prepared-bolt sensor. The linear-feeder loop
runs only until its prepared-bolt sensor turns on. The fastening process owns the
shooting escape, shooting tube, pickup vacuum, and the two-head fastening order.

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

`IBTM.Inspection` owns the shared Inspection Gantry axes, inspection coordinates,
and NG carrier transfer pneumatics and teaching coordinates. `InspectionStation`
runs inspection and NG transfer in one loop because they share the gantry.
`IBTM.NgConveyor` owns the independent NG Shuttle and NG Conveyor. Inspection
observes shuttle readiness through a one-way project reference. The shuttle reads
transfer clearance through `INgCarrierTransferFeedback` in `IBTM.Device`; it does
not reference the Inspection project. A detected carrier does not prove release:
the shuttle waits for Carrier Pickup Up before lowering, including when the
transfer's automatic operation is disabled. Reading this feedback does not enable
the transfer axes or its automatic operation.

Station 3 uses two machine teaching positions for the carrier scan bounds. Scan
overlap belongs to the Inspection Gantry settings. The two locating-pin positions
belong to the shared Carrier Reference because Station 2 and Station 3 use the same
datum without referencing each other's projects. NG pickup and NG shuttle placement
coordinates belong to the NG Carrier Transfer settings. The tile centres, millimetres per
pixel, and original images belong to the recipe image set.

Automatic inspection transforms the same carrier-relative Bolt Points through the
Station 3 locating pins. The camera centres each point and crops the configured square
ROI. A Tiny U-Net model segments the bolt recess, then the ratio of mask pixels above
the mask threshold determines bolt presence. Results are retained by heat sink and bolt
number. A missing bolt makes the carrier NG only after all remaining bolt points have
also been inspected. A Carrier with neither heat sink present skips image capture
and is completed as NG.

NG Conveyor and NG Shuttle are separate mechanical assemblies below Station 3.
The confirmed 260901 IO map, with the user's correction, has P1 at `DI-144`,
P2 at `DI-145`, and P3 on the shuttle at `DI-142`. `DI-146` is absent.
There is one P3/carrier input, owned by the shuttle; raising or lowering the shuttle
does not clear it. Lift feedback remains separate at `DI-140/141`.
The NG sensors are shown as a secondary handling path next to the inspection transfer. NG capacity
counts carriers, not individual PCBs. The Shuttle lowers a new Carrier at
Position 3. With Position 1 empty the belt moves it to Position 1; otherwise it moves
to Position 2, and with Positions 1 and 2 occupied it remains at Position 3. All three
occupied inputs block the next NG pickup at Station 3. A full shuttle stays raised
with its carrier while the lower belt ejects P1 and compacts P2. After eject completion
is acknowledged, the shuttle can lower its retained carrier into the free position.
The eject button releases the Carrier at Position 1. The complete lamp
stays on until the operator removes the ejected carrier and presses the eject-complete
button; operator confirmation has no automatic timeout.

## UI rules

- `MachinePlan` owns shared workpiece dimensions and Station 3 layout metrics.
  Tool centres are calculated from carrier size and camera spacing, not duplicated
  coordinate literals in the view model. The picker/camera use stacked layout;
  NG carriers and their labels share the same Grid rows.
- The NG belt is fixed and centred behind the Station 3 backup plate. Inspection
  motion uses two affine display regions joined at the taught pin-reference line:
  one through the carrier pickup and one through the shuttle handoff. Both consume
  live XY, never a sequence-state animation. This is a readable schematic, not a
  uniform-scale CAD or clearance measurement. Re-teaching changes the projection,
  not the fixed belt position.
- A moving handler is shown only with homed XY and a usable taught display map.
  Unknown positions are not drawn at a fallback origin. These are derived display
  conditions only, not persisted teaching flags or additional motion interlocks.
- Handler badges show stopped/action/waiting information; PCB presence stays on
  the workpiece drawing. Placement uses its existing process-state description.
  Station badges describe the current action only. Heat-sink OK/NG results stay
  inside the matching heat-sink pocket on the carrier and move with it. A station
  waiting to transfer a completed carrier uses the confirmed green state; this is
  readiness, not a duplicate carrier result.
- Feeder-ready feedback stays on the feeder or its matching fastening head. Do not
  repeat it as a detached text legend. NG capacity is read from P1/P2/P3 occupancy;
  show text beside the NG conveyor only for an active move, full condition, or
  required eject action.
- `OperationView` composes the plan in physical drawing order: lower NG conveyor,
  main conveyor and backup plates, then moving handlers. `NgConveyorView` is
  separate from `InspectionView` so the lower conveyor cannot obscure the main
  lane while the shared inspection camera and NG gripper remain above both.
- NG position labels and eject instructions stay outside carrier footprints.
- Shared PCB and carrier XAML resources define their display size. Fixed-size
  handler borders must not change thickness while moving; doing so shifts their
  child geometry relative to the taught-position map.
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
- Keep machine structure subdued. Use green for PCB material, amber for heat sinks,
  blue for carriers and active mechanisms, and red only for alarms that require
  action.
- Keep the palette semantic: application surfaces use the shared neutral scale;
  blue means active motion or selection, green means confirmed/ready, amber means
  attention or an output command, and red means a fault or required operator action.
  Device and workpiece colors come only from AppStyles.xaml and MachineStyles.xaml,
  not view-local color literals.
- Do not show PCB 1 or PCB 2 as present from SMEMA alone. Their presence remains
  unknown until the supply handler's PCB sensor checks each pickup position.
- Show the inspection camera and NG gripper on the same moving Inspection Gantry.
  The camera is mounted on the operator side (down in the plan); the carrier picker
  is behind it (up), not beside it. The picker uses the shared carrier width and
  height, and its jaws span the carrier rather than a PCB-sized footprint.
- Show exactly one main carrier conveyor.
- Show NG Conveyor and NG Shuttle as a secondary path, not as another production
  station or a generic NG Stack box.
- Draw the vertical NG Conveyor in the space behind the main Station 3 lane, as
  corrected by the operator. From top to bottom the positions are P3, P2, P1 / Eject.
  The shuttle loads at the rear P3; the belt pulls carriers forward through P2 toward
  the operator-side P1. Position numbering and digital-input mapping do not change.
  This plan placement is distinct from the lower physical elevation of the NG belt.
  Camera/picker tool centres and the Station 3/NG handoff anchors must change together
  when the schematic geometry changes; moving only the drawn belt breaks live XY
  alignment. UI review teaching belongs to the isolated Virtual verification profile,
  never the machine's saved teaching.
- Keep one screen scale for each physical workpiece. A PCB and a production carrier
  retain the same footprint while moving through handlers, stations, and the NG path.
  WorkpieceStyles.xaml owns the PCB and CarrierView templates; station views bind
  their own presence inputs and work results rather than duplicate the drawings.
  Unknown heat-sink contents on a transported carrier are not inferred from its carrier sensor.
- Show digital inputs as round indicators and outputs as square indicators. Label
  mechanism pairs such as `Grip`, `Stopper`, and `Plate`; tooltips are supplementary.
- Use input state for mechanism confirmation. Output state only shows the command.
- Derive operator states from digital inputs and live motion state. Never infer
  mechanism completion from an output command.
- Apply the same rule in Virtual: actuator feedback changes after the fixed virtual
  delay, then the simulated PCB or carrier position changes.
- Keep axis coordinates as secondary diagnostic information.
- Keep covers and frame geometry subdued so material position and mechanism state
  remain readable. Overhead mechanisms remain translucent enough to preserve the
  carrier, PCB, and heat-sink shape beneath them.

## Remaining mechanical confirmation

The EASM component tree identifies ownership accurately, but the reader currently
does not expose component transforms or bounding boxes. Exact screen coordinates are
therefore an operator-oriented schematic rather than a CAD projection. Axis
directions, travel limits, and physical channel numbers remain editable machine
settings and must be verified during commissioning.
