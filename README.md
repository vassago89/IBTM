# IBTM

.NET 10 WPF control application for a three-station carrier line.
One executable hosts independently enabled machine units and the teaching UI.

## Machine and project boundaries

| Project | Responsibility |
| --- | --- |
| `IBTM` | WPF host, DI, machine-wide start/stop/home/reset, recipe editing and WPF image conversion |
| `IBTM.Storage` | SQLite/EF storage for unit settings, recipes and carrier images; backup/restore |
| `IBTM.Core` | Shared coordinates, images, enums and work results |
| `IBTM.Device` | IO/motion contracts, feedback, cancellation and station primitives |
| `IBTM.PcbSupply` | Supply handler and `PcbSupplier`; upstream PCB-carrier SMEMA |
| `IBTM.PcbBuffer` | Live handoff feedback and position interlock |
| `IBTM.PcbPlacement` | Placement handler and `PcbPlacer`; PCB placement into detected heat sinks |
| `IBTM.BoltFeeder` | Independent pickup and shooting feeder supply loops |
| `IBTM.BoltFastening` | `BoltFasteningStation` work loop and `BoltFasteningGantry` hardware |
| `IBTM.Inspection` | `InspectionStation`, `InspectionGantry`, `BoltInspector` and NG carrier transfer |
| `IBTM.NgConveyor` | Independent NG shuttle and three-position carrier conveyor |
| `IBTM.Conveyor` | Main conveyor, station stoppers/plates, carrier-line SMEMA |
| `IBTM.Inspection.Training` | Tiny U-Net, TorchSharp inference, training, labeling and review UI |
| `IBTM.Ajin` | AJIN motion and RTEX IO |
| `IBTM.AlphaMotion` | AlphaMotion PCIe IO |
| `IBTM.Hantas` | Shared ADC serial bus and individually addressed bolt heads |
| `IBTM.Hik` | Hik area camera |
| `IBTM.Virtual` | Virtual devices and optional material-flow scenario |
| `IBTM.Virtual.Tests` | Motion/IO boundaries, stop/resume and machine-flow regression tests |

The host composes the projects. Hardware-owning objects read their own IO and
expose feedback and operations; automatic units use these objects, not arbitrary
IO numbers. The Operation page displays unit feedback. Digital Inputs/Outputs
windows are the intentional maintenance access.

Supply and Placement coordinate through `BufferStage`; neither calls the other
automatic unit. Stations do not command the main conveyor. The conveyor observes
station inputs and work completion. Inspection depends on the NG shuttle/conveyor,
not the reverse. Inspection and NG carrier transfer share one XY gantry and one
execution loop; there is no inspection Z axis.

Motion coordinates use mm and speeds use mm/s. The default pulse length is
1 µm/pulse (0.001 mm/pulse), editable per motion group in Settings > Motion.
Ajin conversion and Virtual resolution use the same setting; 100 mm/s corresponds
to 100,000 pulses/s at this resolution. Existing saved pulse lengths are preserved;
change them explicitly and restart before using different hardware scaling.

Settings > Motion also owns each group's travel speeds, acceleration/deceleration
times in seconds, X/Y and Z homing speeds, and axis ranges. Existing common home
speeds and AJIN ratios are converted once when the settings database is upgraded;
their equivalent speeds and accelerations are preserved. Home direction, sensor
and method still come from the AJIN `.mot` file. The driver uses pulse units
internally and converts acceleration time to pulses/s². Virtual currently models
travel speed and pulse resolution, not the AJIN acceleration or home-search profile.

`Manual > Dry Run > PCB Return` returns one unfastened PCB from the selected
heat sink through Buffer to Supply. If the carrier is still at Station 2/3,
the main conveyor first returns it to the front sensor, then moves forward to
seat it at Station 1. It finishes conveyor travel there, without advancing to
Station 2. A carrier already at Station 1 only needs seating; an ongoing PCB
handoff resumes without restarting conveyor travel.
`PcbReturn` in the host coordinates existing handler operations and live handoff
feedback without a peer-project reference. Stop retains route intent; Run resumes
from current IO and axis feedback. An unfinished PCB pickup keeps its heat-sink
target even if the selector changes during Stop; Manual shows the actual Active
target separately. The new selection applies to a new return after completion.
Completion leaves the PCB held by the retracted Supply handler. No upstream
placement, bolt operation or new forward cycle starts.
This is one reverse leg, not a complete repeating forward/return machine cycle.

`PCB Round Trip` adds repeated Supply -> selected heat sink -> Supply operation
with one preloaded PCB. It reuses the production transfer/placement actions,
retains direction and target across Stop, and never fetches a second PCB. A full
round trip is one cycle. The carrier stays seated at Station 1.

`NG Conveyor Round Trip` repeats P1 <-> P3 with one carrier. The shuttle must be
Down before either belt direction and rises after the belt has stopped at its
destination. The P3 carrier sensor moves with the shuttle. Stop preserves the
unfinished destination even between sensors; NG pickup Up is required throughout.
This test does not move the NG transfer gantry or operate the main conveyor.
These are independent component tests, not one integrated machine cycle.

`Bolt Route` traverses every taught bolt point on the detected Station 2 heat
sinks: Head 2 PCB points, Head 1 IPM seating points, then Head 1 IPM final points.
It returns through that route and repeats until Stop, using the production
head-specific coordinate conversion. At each bolt point the selected head lowers
and rises without running the driver; XY always moves with both heads raised.
Each IPM seating point also visits the pickup: XY -> Head 1 Down -> pickup Z ->
Safe Z -> Head 1 Up. Vacuum and bolt-detection waits are omitted. IPM final points
do not repick. The return route visits these same locations in reverse order.
Shooting escape, shooting air, vacuum and all associated bolt-supply waits are
omitted. Z stays at Safe Z at fastening points, and moves only at the pickup.
Stop resumes the pending axis/cylinder stroke from live feedback; no fake bolt
sensor or fastening result is created. No ADC command or production completion
is performed. Fastening and loosening are excluded from all dry-run plans;
the separate ADC diagnostic controls and normal production remain unchanged.

See [machine layout](docs/MACHINE_LAYOUT.md),
[Supply behavior](IBTM.PcbSupply/DESIGN.md) and
[Buffer handoff](IBTM.PcbBuffer/DESIGN.md) for detailed mechanical contracts.

For partial hardware arrival, see the
[Station 3 and conveyor commissioning plan](docs/STATION3_COMMISSIONING.md),
including shared XY interlocks, carrier release before Home, teaching persistence
and isolated NG operation. Physical IO boards and the complete mapping are still
installation prerequisites even when some automatic units are disabled.

## Automatic operation and Stop

The machine DB's `UnitSettings` section enables Main Conveyor, PCB Supply, PCB Placement, Pickup Bolt
Feeder, Shooting Bolt Feeder, Bolt Fastening, Inspection, NG Carrier Transfer,
NG Shuttle and NG Conveyor separately. Unit changes apply to the next run.

Inspection and NG Carrier Transfer have separate enable flags but share the
InspectionStation loop. The other enabled automatic units run independently.
Enabling either Supply or Placement requires both handlers' motion feedback and
homing because their shared handoff interlock uses both positions.

`MachineController` owns the run lifetime. Startup hardware checks and automatic
units share the same cancellation token. A unit fault cancels the other units.
Motion objects stop the axes they own on cancellation. Pneumatic outputs are
maintained; conveyor, feeder and shooting run outputs are stopped.
Start remains blocked while a previous operation scope is still active, even if
the axes have stopped. The existing scope count covers manual image acquisition,
scan saving and canceled-operation cleanup; cancellation itself is still immediate.
An unsuccessful home result immediately cancels the other homing axes; horizontal
homing starts only after the preceding Z preparation has completed successfully.
The same rule applies within an AJIN XY group: one failed home cancels its sibling.
Home All and individual-axis Home require all carrier detection inputs to be OFF:
main conveyor entry/stations/exit, NG pickup, NG shuttle and NG conveyor P1/P2/P3.
The operator must raise the cylinders on the units being homed beforehand:
Placement handler, both fastening heads, and NG pickup. Both the Up input and
the absence of Down feedback are checked. Disabled units are excluded from Home
All cylinder checks; an individual Home still checks its selected unit. NG shuttle
height, stoppers and backup plates are not handler-cylinder Home prerequisites.
Inspection Home no longer opens the gripper or raises the pickup automatically.
The separate **RAISE CYLINDERS** button raises the required enabled units together
and waits for their mapped Up/Down feedback, using the configured I/O timeout.
It requires an empty machine and idle, safe Manual operation. It neither moves nor
homes axes and does not change IPM cylinders, grippers, vacuum, stoppers, plates or NG shuttle
height. After the inputs confirm Up, use **HOME ALL** separately. STOP cancels the
feedback waits while retaining pneumatic outputs; a timeout alarms the affected
unit. Repeating the button with cylinders already Up is allowed.
If a required cylinder loses its raised state or a carrier is detected during Home,
the existing cancellation path stops homing. OUTPUTS remains available before
homing when I/O is ready and the machine is idle, safe, alarm-free and in Manual
mode; axis movement remains locked until homed. The Supply rotation/limit-search
home sequence is unchanged.
Automatic, saved-position and Home X/Y moves require raised-cylinder feedback
from its unit: Placement Handler, Fastening Head 1/Head 2, or NG Pickup.
Up must be ON and Down must be OFF. Loss of this condition during X/Y movement
alarms the unit and cancels the machine's operations. Z-only movement is separate;
the condition is checked again before the following X/Y command starts.
Placement enters buffer X/Y with its Handler raised and IPM lowered for pickup.
It keeps IPM Down while carrying the PCB, including the reverse dry-run route.
At the taught heat-sink XYZ with Handler Down, release is vacuum Off -> gripper
Open -> IPM Up -> gripper Close -> IPM Down to press -> IPM Up -> Handler Up -> Safe Z.
IPM lift and gripper feedback do not restrict Home or X/Y movement.
Bolt teaching Jog/Step are separate manual adjustments: they keep the other axes,
including Z, at their current positions and may run with the heads lowered.
They use the selected teaching speed and configured axis limits. Jog stops at its
axis limit or when released/canceled. Manual mode and motion/safety readiness
remain required; switching to Auto or losing readiness cancels the adjustment.
The current motion command identifies adjustment versus positioning; there is no
global interlock-disable switch. Other handlers retain their existing Jog rules.
Fastening raises both heads before each X/Y move, lowers the selected
head at its target, and preserves the PCB → IPM seating → IPM final pass order.
Background Jog failures also report a motion alarm and cancel other operations.

Each automatic loop evaluates live inputs, executes the applicable action and
otherwise waits on relevant IO or motion-state changes. `AsyncAutoResetEvent`
coalesces repeated wake-ups. Live position updates feed the UI and position
interlocks; they are not animation estimates or global sequence ticks.

Material presence comes from DI, not outputs or remembered steps. Paired pneumatic
endpoints include `Between`. PCB selection and work results are progress, not
material sensors. Stop resets Supply's PCB selection to PCB 1; current physical
state takes precedence. Inspection can restart an unfinished inspection from its
first bolt.

Placement, Fastening and Inspection select their heat-sink work targets from DI
when starting a seated carrier. That work list remains fixed until the job ends;
Stop/Start selects it again from current inputs. Presence feedback stays live for
the display, but changing it does not cancel a step, reset completed work or hide
recorded NG results. Carrier seating, mechanism clearance and machine safety
conditions still apply throughout operation.

Work results belong to `HeatSinkAssembly`, keyed by heat sink and bolt number.
The result collections allow concurrent reading by the display, but only recording
methods modify them. Arrival of a new carrier replaces the station's result
collection; an in-flight result cannot attach itself to the next carrier.
Results transfer to the next station on its carrier-arrival input.
Recovery dialogs apply only the items they displayed: unchecked items are marked
for rework, checked items keep existing results, and omitted items are untouched.
Confirming a dialog does not re-read presence inputs to decide which records to
clear. Existing measured torque and NG results are not replaced by manual OK.
Disabled station work is pass-through without the conveyor marking it complete;
NG pickup still requires a seated carrier, even with inspection disabled.

## Material flow

Only one carrier moves on the main conveyor at a time. Raised backup plates
mechanically separate carriers being processed from that conveyor.

The main conveyor chooses rear-to-front:
Inspection to rear, Fastening to Inspection, Placement to Fastening, then receive.
Before motion it lowers the source stopper/plate, raises the destination stopper
and lowers its plate. It stops at the destination carrier sensor, raises the plate
and lowers the stopper. Stations process the heat sinks detected at job start;
an empty carrier passes through, and Inspection marks an empty inspection job NG.
Completed inspection results, rather than later presence changes, determine the
normal/NG route. With inspection disabled, the NG Transfer enable setting
determines the bypass route.

The Supply PCB-carrier SMEMA and main-conveyor SMEMA are separate.
Main-conveyor Front Ready drops when the entry sensor detects the carrier.
Rear discharge completes after the exit sensor turns ON then OFF.
An already active entry/exit sensor takes priority when resuming.
An in-progress transfer keeps its destination/phase across Stop, including NG
conveyor compaction between sensors. This is motion progress, not remembered
carrier presence, and is not persisted. After a program restart, a carrier between
sensors with all related inputs OFF cannot be located automatically.
An arrived carrier resumes seating without lowering its backup plate again;
an already reached NG destination does not restart the conveyor motor.

Supply X/Y move individually through `IAxisMotion`; Placement, Fastening and
Inspection use `IXyMotion`. Supply checks both PCB positions independently and
returns to Rotation Z even after an empty PCB 2 check. It then releases the
upstream carrier while any picked PCB continues through the buffer handoff.
Rotation turns the PCB over vertically by 180 degrees.

Placement secures the buffered PCB and IPM before Supply releases its fixer.
It places PCBs in all detected heat sinks before completing Station 1.
The buffer keeps no software owner or reservation: its interlock uses live
positions, in-position feedback and taught handoff coordinates.

Fastening uses one XYZ gantry with two ADC-controlled heads:

1. Head 2 shoots and fastens all PCB bolts.
2. Head 1 picks and fastens all IPM bolts using the seating preset.
3. Head 1 revisits all IPM bolts using the final preset.

Both IPM-pass results are retained. A fastening NG result does not skip remaining
bolts or the other heat sink. The feeder loops prepare bolts; the fastening gantry
owns escape and shooting actions.
ADC controller errors and communication failures are machine faults, not ordinary
fastening NG results. The shooting passage sensor is watched before the shooting
output turns on so a short ON/OFF pulse is not missed.
Virtual also retains a detected tube bolt when shooting air stops; air OFF is not
proof that the tube is clear. Manual shooting can deliver that retained bolt
with the existing escape and vacuum states, without advancing a new bolt.

Inspection moves to each taught bolt point and checks presence. NG carriers move
on the shared gantry to the independent NG shuttle, then onto the vertical NG
conveyor at position 3. The conveyor fills position 1 first, then 2, then 3.
The alarm carrier count is configurable.
At the shuttle, the NG pickup confirms the gripper open and shuttle carrier input
before raising. Automatic and dry-run restart finish that release without closing
the gripper again during raising, even if
the pickup's carrier sensor still detects the released carrier.
Dry run changes to the return direction only after pickup retraction completes.

With Inspection disabled and NG Carrier Transfer enabled, Station 3 carriers route
to NG. Disabling transfer prevents its automatic motion/IO, not its physical
interlocks: the pickup must be raised before the NG Shuttle lowers, and the transfer
must be clear before Station 3 receives a carrier or begins inspection.

## Settings, teaching and storage

Each `Setting` is stored as a separate JSON row in `Data/Machine.db` beside the executable.
Units receive only their relevant settings; `MachineSettings` is the host aggregate.

- `DriverSettings`, `UnitSettings`, `MachineOptions`: machine operation.
- `*HardwareSettings`: responsibility-owned logical IO and axis mappings.
- Unit settings: travel/home speeds, acceleration times and taught positions;
  each unit's motion hardware settings own its axis ranges and pulse length.
- `CarrierReferenceSettings`: inspection upper-left/lower-right locating pins.
- `NgCarrierTransferSettings`: carrier pickup, shuttle placement and transfer speed.
- `NgConveyorSettings`: NG conveyor behavior and alarm count.
- `AjinSettings`, `AlphaMotionSettings`, `HantasSettings`, camera/lighting settings:
  device connections and driver configuration.

Mapping numbers 0–15 address AlphaMotion IO. Remaining numbers address RTEX points,
using separate input/output module arrays in `AjinSettings`. Do not infer an AJIN
module ID from a logical IO name. Mapping and driver changes apply after restart.
AJIN loads its configured `.mot` file; motion coordinates exposed to units are
millimetres, converted from pulses using the configured millimetres-per-pulse.

Output mappings include their ON/OFF feedback inputs.
Actuator completion requires the requested endpoint ON and the opposite endpoint
OFF. Contradictory feedback remains pending until corrected or timed out.
`MachineOptions.TimeoutMilliseconds` is the common actuator timeout.
Bolt feeders have their own supply timeout. A timeout belongs to the issuing unit.

Normal motion requires completed homing and Servo ON. Supply initialization has
its dedicated rotated, lower-Z-limit clearance path; see its behavior document.
Other Z axes home before horizontal axes. Supply uses Rotation Z, Placement uses
Buffer Entry Z and Fastening uses Safe Z for horizontal travel.

Each head teaches the carrier's upper-left and lower-right locating pins.
Bolt points are carrier-relative recipe coordinates. Station 3 captures original
frames at machine-XY positions; WPF places them at their captured centres without
creating a stitched bitmap. The operator adjusts millimetres-per-pixel and teaches
pins/bolt points on that map. Scan motion does not depend on the image scale.

```text
Data/Machine.db
TrainingData/BoltTraining.db
```

Settings and recipes are JSON records in SQLite; original carrier PNGs are BLOBs.
Recapture and Save As commit images and recipe metadata in one transaction.
Previous data remains intact on a failed save. Image numbering restarts at 1 for
each replacement scan. No loose recipe images or settings JSON files are written.
See [settings ownership, migration and backup](docs/SETTINGS_STORAGE.md).
The recipe toolbar stays disabled throughout teaching, capture and recipe
commands, including their stationary imaging/saving intervals.
Recipe Save/Load also hold an operation scope until DB work finishes, keeping
Auto Start unavailable. Their commands disable teaching-page edits for the same
interval; the operation page and Stop remain accessible.

The inspection recipe selects a centred square ROI, resized to the model's fixed
128×128 input. Exposure, gain, lighting and scan overlap are also product-owned.
Training and inference use the same preprocessing. The count of pixels at or above
the training setting's MaskThreshold, divided by ROI pixels, is compared with the
inspection recipe's MinimumMaskRatio. The model threshold stays with Bolt Training;
operators can tune the product's area limit without changing U-Net settings.
Training runs in the application, outside automatic operation, using CPU TorchSharp.
It saves the model and reviews validation masks; no separate training executable
is required. Original recipe camera frames remain available.

Hardware choices are independent: Control is Virtual/Physical, Camera is
Virtual/Hik and Bolt is Virtual/HantasAdc. Inspection Algorithm independently
selects Simulated or Tiny U-Net, so a Virtual camera can run the real trained model.
Tiny U-Net requires a saved model; it does not silently substitute simulated results.
Model readiness is checked before automatic units start. Initial setup, Home,
camera teaching and training remain available before the first model is created.
With physical hardware and an enabled Simulated inspection, the environment badge
shows Mixed rather than Physical.
Virtual IO produces mapped actuator DI
feedback after a delay; the optional `VirtualMachine` scenario supplies material
sensor transitions. Digital Inputs can be toggled in Virtual mode, including
during Auto and Home. Physical inputs remain read-only.

The Virtual-only **Auto Response** switch in Digital Inputs defaults to ON.
Turn it OFF to drive sensors manually: DO and motion keep running, but mapped
feedback and simulated material/feeder responses stop. Pending responses are
discarded even if the switch is immediately turned back ON. Re-enabling applies
the current DO-to-DI mapping after the usual delay; it does not replay material
transfers. Current DI and positions are not reset. Door, emergency-stop and reset
simulation remain active in either mode. This switch is session-only, not a
machine setting.

Digital Inputs supports text search and a unit filter. Filtering only changes the
visible rows; all inputs continue updating. IO numbers remain in Settings.

Supply and Station Teaching share a read-only **Related I/O** panel. Selecting a
point/unit selects its handler and related buffer, feeder or station signals.
`IoSignals` creates one read-only signal object per configured DI/DO.
`InputHardwareSettings.CreateIoStatus` and `ConveyorStation.CreateIoStatus` select
from these same objects; they do not subscribe or copy state again. The host
composes related units once through DI, with no per-signal lists in the views.
Input, Output and both teaching pages share these objects. DI is blue, DO is
amber, and each mapped output shows both feedback inputs independently. Values
read the IO service directly and changes notify only the affected rows. The panel
does not issue outputs or add timers. Output commands retain only their own
waiting/timeout state. Both diagnostic windows share the generic `IoList` filter
and XAML search/feedback templates. Each hardware-owning project supplies its
signal sections through `HardwareSettings.GetSection`, also used by Settings.
Mapping changes apply after restart.

Each motion hardware definition declares its group and X/Y/Z signal mapping once.
Driver construction, Settings and the manual axis list reuse this definition;
Inspection does not acquire a Z axis through a separate UI assumption.
Typed machine settings remain responsibility-owned. The host persists settings,
recipes and carrier images in `Data/Machine.db`; `RecipeStore` handles the WPF image
conversion boundary. Training data/model remain in `TrainingData/BoltTraining.db`.
There are no separate settings JSON or recipe image files; see
[settings storage](docs/SETTINGS_STORAGE.md) for backup and ownership.

One RS-422 bus addresses both ADC controllers. Presets are configured on the
controllers; recipes select preset numbers. `AdcBus` serializes requests and
`VirtualAdcBus` implements the same protocol interface offline.
The ADC diagnostic window remains available in safe, idle Manual mode for
communication and alarm reset, including before homing. Fastening tests require
the normal machine-ready conditions and exclude automatic operation, motion,
home and reset until the ADC Stop request completes. Raw Remote Start is rejected;
use Start Fastening or Reverse (Hold). Reverse runs only while held and stops on
release, pointer exit/capture loss, STOP or window close. It uses the selected
controller's loosening settings, never moves machine axes/cylinders/feeders and
does not report automatic loosening completion. These diagnostic commands are
separate from dry run, which never starts fastening or loosening.
With a Virtual ADC, the window also stays available during Auto for **Next Result**:
select the slave, choose OK/NG/Error and click **Apply Once**. The next fastening
start on that controller consumes the result; an already running fastening is
unchanged. Other protocol commands retain their Manual/safety interlocks.
An injected Error remains active across preset/direction changes until ADC Alarm
Reset; attempts to Start during the error do not consume a queued result.
The MOVS light controller uses 19200 baud and the existing
`:L{channel}{level:000}\r\n`, `:O{channel}\r\n`, `:F{channel}\r\n` commands.

## Build and validation

```powershell
dotnet build IBTM.slnx --configuration Release
dotnet test IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj --configuration Release
dotnet run --project IBTM/IBTM.csproj
```

### Offline development

```powershell
dotnet run --project IBTM/IBTM.csproj -c Virtual --launch-profile Virtual
```

In Visual Studio, select the **Virtual** configuration and launch profile with
`IBTM` as the startup project. This build uses `bin/Virtual/net10.0-windows`, its
own databases and a separate application mutex. It always selects Virtual
control, camera and bolt hardware, even if those saved driver fields are changed.
Debug/Release do not accept the `--virtual-development` argument.

On the first clean launch, it creates synthetic teaching settings and a **Virtual
Development** recipe. Subsequent launches preserve saved settings and recipes.
It does not automatically Home or Start. These coordinates are demonstration
values, not machine teaching data.

For file-based inspection, select **Tiny U-Net** under Settings / Controllers,
save and restart, then use **Open Image**. Each Virtual camera capture returns that
same full-size image. **Use Generated** restores the synthetic camera. The image
selection is session-only; files must be at least 128×128 pixels. This is a fixed
image source, not position-dependent playback.

**Bolt Training / Add Images** accepts PNG/JPEG/BMP/TIFF files without homing or a
carrier. Mark the bolt recess, or mark an empty sample, and train on CPU in the
same application. It retains original frames while labeling; the existing centred
128×128 training ROI is unchanged. Actual **Capture Bolt Points** still requires
homing, ready motion and a seated carrier. See the
[training guide](IBTM.Inspection.Training/README.md).

Virtual tests do not certify physical wiring, pneumatic timing, camera optics,
servo parameters or machine clearances. Confirm those with the actual equipment.

Closing the main window cancels active operations immediately and waits for
their cleanup, active UI commands and ADC Stop before disposing DI and devices.
The existing operation-token scopes also cover background Jog cleanup and the
last motion-feedback event; `IsMoving == false` alone is not a completion barrier.
Normal Stop remains restartable. Window shutdown blocks new operations, retains
pneumatic outputs, and turns run/SMEMA outputs off. No fixed shutdown delay is used.
Forced process termination and power loss cannot use this cooperative close path.
Repeated Stop can overlap operation completion: an active cancellation callback
retains its scope until the callback returns, without running callbacks under a lock.

Physical IO reconnection drains the previous input monitor before starting another.
Hardware readiness runs outside the input callback so a hardware Reset cannot wait
for its own input-monitor task to end.

Manual teaching uses the same Stop scope for the whole command, including IO
feedback waits and the next axis in a multi-axis move. Unsaved teaching moves use
the displayed point, including Supply buffer Z; saving remains explicit.
Station 2 restart preparation retains measured torque and OK/NG results for
checked operations. Unchecking an operation selects rework and removes its result;
a checked operation without a measured result is recorded as manual completion.

`artifacts/ShutdownVerification` closes the actual Virtual-mode app during home,
supply movement, fastening, inspection movement, synchronous camera capture and
manual Jog, checking cleanup before DI disposal. All six scenarios passed.
