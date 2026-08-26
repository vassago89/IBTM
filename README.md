# IBTM

IBTM is a .NET 10 WPF machine-control application for a three-station carrier-jig line.
The stations work independently while sharing one IO-driven belt conveyor.

## Machine layout

1. PCB Supply Handler has X/Y/Z motion, an IPM fixing cylinder, and pneumatic rotation.
2. PCB Placement Handler has XYZ motion and transfers the PCB from the buffer to a housing.
3. Bolt fastening has one shared XYZ motion group and two fastening heads.
4. Inspection camera and NG carrier gripper share one Inspection Gantry XY motion.
   NG Shuttle and NG Conveyor are separate from the main production conveyor.

The conveyor moves only one carrier jig at a time. A carrier jig lifted by a station
backup plate is mechanically separated from the conveyor, so another carrier jig can
move between other stations.

The CAD-derived mechanical grouping and its UI mapping are documented in
[Machine Layout](docs/MACHINE_LAYOUT.md).

Supply and Placement each have a dedicated PCB-presence input. A missing PCB at
supply position 1 advances
the handler to position 2. A missing PCB at position 2 completes the upstream
carrier without rotation or buffer transfer.

## Projects

```text
IBTM/                               WPF host, settings, persistence, DI, UI
IBTM.Core/                          Shared positions, images, enums, and results
IBTM.Device/                        Hardware boundaries, shared IO/motion primitives, lighting
IBTM.Conveyor/                      Main conveyor, Front 2/Rear SMEMA, plates, and stoppers
IBTM.Ajin/                          AJIN motion and RTEX I/O
IBTM.AlphaMotion/                   AlphaMotion PCIe I/O
IBTM.Hantas/                        Hantas ADC bus and bolt-head implementation
IBTM.Hik/                           Hik area-camera implementation
IBTM.PcbBuffer/                     PCB buffer position interlock
IBTM.PcbSupply/                     Supply handler and automatic PCB-carrier process
IBTM.PcbPlacement/                  Placement handler and Buffer pickup process
IBTM.BoltFeeder/                    Pickup and linear bolt feeder supply loops
IBTM.BoltFastening/                 Fastening station and automatic fastening process
IBTM.Inspection/                    Inspection capture, state, and bolt-presence decision
IBTM.Inspection.Training/           Tiny U-Net model, inference, training, review, and WPF page
IBTM.NgConveyor/                    NG shuttle, three-position conveyor, and eject hardware
IBTM.Virtual/                       Virtual hardware
IBTM.Virtual.Tests/                 Critical virtual safety checks
```

Each physical-device owner reads its own IO. Automatic process classes use those
owners' semantic properties and operations instead of `InputIo` or `OutputIo`.
The Operation page reads all IO for display, while the manual Digital Inputs and
Outputs windows are the intentional maintenance bypass.

Supply and Placement process classes do not receive `IIoService`. Their handlers
read only their own device IO, `BufferStage` owns the Buffer PCB input, and
both processes coordinate through live Buffer positions and handoff feedback.
Placement, Bolt Fastening, and Inspection own a small Work state that reads their
Carrier Jig, Housing, Backup Plate Up, and Stopper Down inputs. Their processes know nothing about
`MainConveyor`; the conveyor observes those Work states when selecting a transfer.

Paired cylinder inputs are represented as endpoint states with an explicit
`Between` value. Process-local values such as PCB 1/2 selection, completed Housing,
or station work completion are progress only; they are never used as proof of
material or mechanism position.

`BufferStage` reads live Supply and Placement positions. It stores no reservation
or owner. Both handlers may overlap only at their taught handoff positions while
Placement secures the PCB with vacuum and the loose IPM with its gripper. Placement
then waits while Supply detects that feedback, releases its IPM fixer, and leaves.

Position events drive only the live handler drawing and Buffer collision check.
Processes wake on Buffer input changes and motion start/end, so 10 ms position
updates do not refresh the whole UI or accumulate process wake-ups.

`MachineState.AutomaticRunning` remains true while enabled processes wait for
inputs. Homing and manual controls therefore stay disabled even when every axis
is temporarily stopped.

Station motion has no global execution lock. PCB supply and placement share only
the Buffer position interlock; bolt fastening and inspection remain runnable while a
Buffer transfer is active. `PcbSupply` owns the independent Front 1 PCB-carrier
SMEMA. `MainConveyor` owns the housing Carrier Jig belt, Front 2/Rear SMEMA,
station stoppers, and backup plates. Stations do not request conveyor movement.
`MainConveyor` selects one transfer at a time in rear-to-front priority: Inspection
to Rear, Bolt Fastening to Inspection, PCB Placement to Bolt Fastening, then Front 2
to PCB Placement. It lowers the source Stopper and Backup Plate, raises the destination
Stopper, and lowers the destination Backup Plate before running the belt. It stops on
the destination Carrier input, raises the destination plate, then lowers the Stopper.
Its `ConveyorState` is recalculated from live station inputs, work completion, and
SMEMA inputs on every change; no transfer step or conveyor ownership is cached.
Front Available is the receive trigger. PCB Placement raises its Stopper and lowers
its Backup Plate before asserting Front Ready and starting the belt. Rear Available
may remain advertised while waiting for Rear Ready, but is cleared before a Front
receive starts. Transfer decisions never use output state as physical evidence.

Supply may receive, pick, and rotate the next PCB while Placement is working. It
waits with PCB detection, Nest, and IPM-fixer feedback confirmed, then enters
when Placement is at or above Buffer Entry Z. The taught handoff is the only
permitted overlap below that Z.

There is no global sequence project. `PcbSupplyProcess` runs the upstream carrier and
PCB 1/2 supply flow. Placement, Bolt Fastening, and Inspection report work complete
after all present housings finish. Inspection moves the camera to every taught bolt
point, records presence by housing and bolt number, and continues after a missing
bolt. The WPF host's `MachineController` initializes the machine and handles stop,
emergency stop, reset, and safety-input changes.

`UnitSettings` enables Main Conveyor, PCB Supply, PCB Placement, both Bolt Feeders,
Bolt Fastening, and Inspection independently. Supply and Placement automatic processes can be
enabled separately, but enabling either requires both handlers to be homed because
the shared Buffer interlock reads both live positions. Other disabled hardware is not
initialized and does not participate in readiness or machine-wide homing. Settings and
manual I/O remain available, and a disabled main-conveyor station is bypassed for
individual hardware validation. Main Conveyor, PCB Supply, PCB Placement, both Bolt
Feeders, Bolt Fastening, and Inspection run continuously.
Placement completes the Buffer handoff, moves above Housing 1 at Buffer Entry Z,
rotates, and waits. When the carrier jig and Backup Plate are confirmed, it places
PCBs only in detected housings. An NG carrier remains at Station 3 until the separate
NG transfer and conveyor behavior is defined.

`MachineController` owns the lifetime of enabled automatic processes. It starts each
process with one shared cancellation token. When any process ends, the token is cancelled for the
remaining processes and owned devices are stopped. A process has no polling timer: it
evaluates live inputs, performs one valid action, and otherwise waits for the relevant
I/O or shared Buffer state to change before evaluating again.

Station work completion remains valid across Stop while the same Carrier input is
on, and resets when that input changes. This lets a released source Carrier resume
its transfer without adding a conveyor-step cache. A Carrier stopped between station
sensors cannot be located automatically.

`VirtualIoService` is a virtual I/O board and produces only mapped actuator
feedback. `VirtualMachine` is the optional material-flow scenario that changes
PCB and housing inputs from virtual motion and actuator results. Production code
observes the same digital inputs in either mode.

## Handler behavior

Handler-specific behavior is documented beside the project that owns it. These
documents are the behavior contract used while automatic state logic is being
implemented:

- [PCB supply handler](IBTM.PcbSupply/DESIGN.md)
- [PCB buffer stage](IBTM.PcbBuffer/DESIGN.md)

Each handler document records its responsibility, input-derived state, compound
operations, normal flow, and behavior that has intentionally not been defined yet.

## Teaching model

Each motion group owns its directly taught positions. PCB placement stores both housing
positions, each fastening head stores the upper-left and lower-right locating-pin
positions, and inspection stores its own scan and locating-pin positions. Bolt points
are carrier-relative recipe coordinates. No hidden calibration is applied between
stations.

## Hardware configuration

Every `Setting` type is stored in its own JSON file under `Settings`. Files are
grouped by responsibility:

- operation: `DriverSettings`, `UnitSettings`, `MachineOptions`, `HomeSettings`;
- device connection: `AjinSettings`, `AlphaMotionSettings`, camera, lighting,
  and `HantasSettings`;
- machine teaching: Buffer, Supply, Placement, Bolt Fastening, and Inspection
  settings; and
- physical mapping: the responsibility-owned `*HardwareSettings` files below.

Logical `InputIo`, `OutputIo`, and `MachineAxis` values map to physical channels
in these hardware files:

- `MachineHardwareSettings.json`
- `ConveyorHardwareSettings.json`
- `PcbSupplyHardwareSettings.json`
- `PcbBufferHardwareSettings.json`
- `PcbPlacementHandlerHardwareSettings.json`
- `PcbPlacementStationHardwareSettings.json`
- `BoltFeederHardwareSettings.json`
- `BoltFasteningHardwareSettings.json`
- `BoltFasteningStationHardwareSettings.json`
- `InspectionStationHardwareSettings.json`
- `InspectionGantryHardwareSettings.json`
- `NgShuttleHardwareSettings.json`
- `NgConveyorHardwareSettings.json`

Input-only settings do not expose outputs or axes. IO settings do not expose axes.
Only motion settings contain axis number, direction, range, and pulse length. In
physical mode, setting numbers `0-15` are AlphaMotion `000-00F`. The remaining
numbers are contiguous RTEX points: `16` is `100`, `48` is `120`, and `80` is
`140`. The RTEX rack uses input modules `0`, `1`, `4` and output modules `2`, `3`,
`4`; module `4` is the shared DI16/DO16 card. AJIN and Virtual consume the same
settings for their own motion group.
Driver, device-connection, I/O-channel, and axis-mapping changes take effect
after application restart.
Hardware drivers are selected independently:

- `ControlDriver`: `Virtual` or `Physical`
- `CameraDriver`: `Virtual` or `Hik`
- `BoltDriver`: `Virtual` or `HantasAdc`

The inspection camera is the only camera currently controlled by the application.
Automatic bolt inspection captures each taught bolt point and passes the centered
128 x 128 ROI through `IBoltRecessSegmenter`. The physical implementation and all
TorchSharp/Tiny U-Net code live in `IBTM.Inspection.Training`; `IBTM.Inspection`
only owns capture and the presence decision. The model returns a bolt-recess probability mask;
the ratio of pixels above `MaskThreshold` is compared with `MinimumMaskRatio`.
Virtual mode produces the same mask contract without loading model weights.
`IBTM.Inspection.Training` trains the same C# model on CPU from paired camera images
and binary masks. The training workflow does not run during automatic operation.
The Bolt Model Training page captures taught points, labels the recess, trains
`BoltRecess.dat`, reloads it, and compares each validation prediction with its
label. The validation set selects and saves the live minimum-mask ratio.
Station 3 scans the carrier with the Inspection Gantry and saves each original frame in the
recipe's `Carrier` directory. WPF places those frames at their captured machine-XY
centres; it does not create a stitched bitmap. The operator adjusts the recipe's
millimetres-per-pixel value until overlapping frames align, then clicks the carrier
pins and bolt locations directly on that machine-coordinate image map. Scan bounds
and X/Y capture pitches belong to `InspectionGantrySettings.json`; capture motion does
not depend on the image scale being calibrated.

Recipe assets are grouped by recipe:

```text
Recipes/<Recipe Name>/Recipe.json
Recipes/<Recipe Name>/Carrier/0001.png
Recipes/<Recipe Name>/Carrier/0002.png
```

Each IO setting pairs its pneumatic outputs with the input state that
confirms its ON and OFF motion. `MachineOptions.TimeoutMilliseconds` is the common
feedback timeout.
Grippers, stoppers, backup plates, and the supply rotation use this map. SMEMA,
lasers, tower lamps, and the buzzer have no direct actuator feedback and are not
included. A feedback timeout fails the caller that commanded the output.

The bolt-fastening motion group has one shared XYZ motion and two heads. Each head
teaches the carrier's upper-left and lower-right locating pins in its own machine
coordinates. Head 1 is the pickup head. Head 2 is the shooting head. Head 1 uses
its own vacuum pump and vacuum sensor when picking from the taught ZEDA KS1069C-S
pickup index. One RS-422 bus connects both ADC controllers. Slave address 1 drives
the pickup head and slave address 2 drives the shooting head by default; slave
addressing is independent from mechanical head numbering. `IBTM.BoltFeeder` owns
bolt feeding only: the pickup-feeder sensor, linear-feeder detection, and the mapped
feeder-run signal. `IBTM.BoltFastening` owns the shooting-tube sensor, both escape
endpoints, escape output, and shoot output used to bring the prepared bolt to the
fastening head. When the linear-feeder sensor is off, the feeder loop turns the run
signal on and turns it off as soon as the sensor detects the prepared bolt. Recipes select
`Shooting` or `Pickup` for every bolt point. The shooting Escape is confirmed backward
before the shared XYZ motion moves to a fastening point.

The two `IBoltHead` instances are independent from motion and inspection hardware.
`VirtualAdcBus` simulates both addressed controllers through the same protocol API.
`AdcBoltHead` selects the recipe preset, starts fastening through remote control,
and reads event results until the controller reports
fastening OK, NG, or error. `AdcBus` owns the single COM port and serializes all
requests. The settings contain one port, one baud rate, and one slave address per
head. `BoltFasteningStation`
selects the requested head and passes the shared equipment cancellation token through
to either driver, so Stop cancels an active Virtual or ADC fastening call.

Automatic horizontal movement starts at the Z taught by that project.
Supply uses Rotation Z, Placement uses Buffer Entry Z, and Bolt uses Safe Z.
Supply receives only `IAxisMotion` and moves Y, then X. Placement, fastening,
and inspection receive `IXyMotion`, which additionally permits coordinated XY.
The concrete AJIN and Virtual motion classes are not registered in IoC.
Manual horizontal jog is enabled only while the motion is at that project's Z.

Supply Buffer exit is the one work-cycle exception. After releasing the PCB at
Place Z, Supply moves farther down to Clear Z and exits to the X home position
with Y fixed. The motion layer permits this horizontal move only at the configured
Clear Z; the handler additionally requires the rotated-position input.

`MoveZToPositiveLimitAsync` is the homing/recovery exception for a Supply Z axis.
AJIN uses
`AxmMoveSignalSearch` to stop on the logical positive limit; Virtual moves to the
configured Z maximum and raises the same limit state. Reaching this limit does not
mark the axis as homed. Supply initialization confirms the rotated input, moves Z
to this lower limit, homes X/Y, and finally homes Z
at the upper end. An empty unrotated handler rotates before any Z movement. Homing
is blocked only for `Unrotated && (Supply PCB present || Buffer PCB present)` and
while neither rotation endpoint is confirmed.

When any motion axis has not been homed since controller startup, normal operation,
teaching, and manual outputs are disabled. The process screen exposes `Home All`
directly and homes all Z axes first, moves them to each project's taught Z,
then homes the remaining horizontal axes in parallel. Supply uses its dedicated
lower-Z-clearance path described above. A safety-input change cancels homing and
stops the equipment, including homing started from Settings.

The process monitor shows live supply, placement, bolt-gantry, and NG-transfer
positions over an operator-oriented top view. The moving inspection optics, one main
carrier conveyor, NG Conveyor, and NG Shuttle are shown
as distinct mechanisms. The Settings screen contains the complete mapped I/O and
axis view.

The map does not infer material from a command or SMEMA alone. PCB is green, housing
is amber, and carrier jigs are blue. The two upstream PCB slots remain marked unknown
until the supply pickup sensor checks them. A pneumatic feedback timeout raises an
alarm on the supply or placement handler that issued the command.

One MOVS light-controller channel drives the inspection light. Its channel and level
are machine settings.
Its serial protocol is:

```text
19200 baud
:L{channel}{level:000}\r\n
:O{channel}\r\n
:F{channel}\r\n
```

## Build

```powershell
dotnet build IBTM.slnx --configuration Release
dotnet test IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj --configuration Release
dotnet run --project IBTM/IBTM.csproj
```
