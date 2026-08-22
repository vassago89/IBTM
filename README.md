# IBTM

IBTM is a .NET 10 WPF machine-control application for a three-station carrier-jig line.
The stations work independently while sharing one IO-driven belt conveyor.

## Machine layout

1. PCB Supply Handler has X/Y/Z motion, an IPM fixing cylinder, and pneumatic rotation.
2. PCB Placement Handler has XYZ motion and moves the PCB over a fixed alignment camera.
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
IBTM.Conveyor/                      Main conveyor, SMEMA, station plates, and stoppers
IBTM.Ajin/                          AJIN motion and RTEX I/O
IBTM.AlphaMotion/                   AlphaMotion PCIe I/O
IBTM.Hantas/                        Hantas ADC bus and bolt-head implementation
IBTM.Hik/                           Hik area-camera implementation
IBTM.PcbBuffer/                     PCB buffer position interlock
IBTM.PcbSupply/                     Supply handler and automatic PCB-carrier process
IBTM.PcbPlacement/                  Placement handler and Buffer pickup process
IBTM.BoltFastening/                 Fastening station and automatic fastening process
IBTM.Inspection/                    Inspection inputs, shared gantry, and NG carrier pickup
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
Placement consumes the semantic `PcbSupplyProcess.ReadyForHandoff` condition.
Bolt fastening follows the same rule: its process reads housing and Backup Plate
state through `BoltFasteningStation`.

`BufferStage` reads live Supply and Placement positions. It stores no reservation
or owner. Both handlers may overlap only at their taught handoff positions while
Placement secures the PCB with vacuum and the loose IPM with its gripper. Placement
then asks Supply to release its IPM fixer and leave.

Position events drive only the live handler drawing and Buffer collision check.
Processes wake on Buffer input changes and motion start/end, so 10 ms position
updates do not refresh the whole UI or accumulate process wake-ups.

`MachineState.AutomaticRunning` remains true while enabled processes wait for
inputs. Homing and manual controls therefore stay disabled even when every axis
is temporarily stopped.

Station motion has no global execution lock. PCB supply and placement share only
the Buffer position interlock; bolt fastening and inspection remain runnable while a
Buffer transfer is active. `MainConveyor` owns the one physical belt, SMEMA,
station stoppers, and backup plates. Stations do not request conveyor movement.
The three Carrier Jig arrival inputs are mapped. Automatic carrier transfer is not
implemented yet; `MainConveyor` currently owns motor Run/Stop and SMEMA outputs.

Supply may receive, pick, and rotate the next PCB while Placement is working. It
waits with PCB detection, Nest, and IPM-fixer feedback confirmed, then enters
when Placement has left the Buffer area.

There is no global sequence project. `PcbSupplyProcess` runs the upstream carrier and
PCB 1/2 supply flow. `BoltFasteningProcess` independently watches the Station 2
backup-plate and housing inputs, then runs only the bolt points belonging to the
present housings. The remaining station automation is added independently. The WPF
host's `MachineController` initializes the machine and handles stop, emergency stop,
reset, and safety-input
changes.

`ProcessSettings` enables PCB Supply, PCB Placement, Bolt Fastening, and Inspection
independently. A disabled process does not participate in machine readiness or
machine-wide homing, while its teaching and manual I/O remain available. PCB Supply,
PCB Placement, and Bolt Fastening currently run continuously.
Placement currently completes the Buffer handoff and moves to Fiducial 1; alignment
and housing placement are the next undefined part. Conveyor, Inspection, and NG
Conveyor processes are added when their input-driven behavior is defined.

`MachineController` owns the lifetime of enabled automatic processes. It starts each
process with one shared cancellation token. When any process ends, the token is cancelled for the
remaining processes and owned devices are stopped. A process has no polling timer: it
evaluates live inputs, performs one valid action, and otherwise waits for the relevant
I/O or shared Buffer state to change before evaluating again.

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

Alignment-camera positions are separate machine motions:

```text
Pick PCB
  -> rotate supply handler
  -> place PCB on Buffer Stage
  -> placement handler picks PCB from Buffer Stage
  -> Fiducial 1 capture
  -> Fiducial 2 capture
  -> average X/Y correction
  -> taught PCB place position + correction
```

The alignment camera does not calculate PCB rotation or scale.

## Hardware configuration

Every `Setting` type is stored in its own JSON file under `Settings`. Files are
grouped by responsibility:

- operation: `DriverSettings`, `ProcessSettings`, `MachineOptions`, `HomeSettings`;
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

There are two cameras: `AlignmentCamera` and `InspectionCamera`. Station 3 scans the
carrier with the Inspection Gantry and saves each original camera frame in the
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
the shooting head and slave address 2 drives the pickup head by default; slave
addressing is independent from mechanical head numbering. Feeder control and feedback wiring remain pending. Recipes select
`Shooting` or `Pickup` for every bolt point.

The two `IBoltHead` instances are independent from motion and camera hardware.
`VirtualBoltHead` returns the requested torque immediately for offline operation.
`AdcBoltHead` selects the recipe preset, starts fastening through remote control,
and reads event results until the controller reports
fastening OK, NG, or error. `AdcBus` owns the single COM port and serializes all
requests. The settings contain one port, one baud rate, and one slave address per
head. `BoltFasteningStation`
selects the requested head and passes the shared equipment cancellation token through
to either driver, so Stop cancels an active Virtual or ADC fastening call.

Automatic horizontal movement starts at the motion group's configured `SafeZ`.
Supply receives only `IAxisMotion` and moves Y, then X. Placement, fastening,
and inspection receive `IXyMotion`, which additionally permits coordinated XY.
The concrete AJIN and Virtual motion classes are not registered in IoC.
Manual horizontal jog is enabled only while that motion group is at `SafeZ`.

Supply Buffer exit is the one work-cycle exception. After releasing the PCB at
Place Z, Supply moves farther down to Clear Z and exits to the taught outside X
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
directly and homes all Z axes first, moves them to their configured Safe Z positions,
then homes the remaining horizontal axes in parallel. Supply uses its dedicated
lower-Z-clearance path described above. A safety-input change cancels homing and
stops the equipment, including homing started from Settings.

The process monitor shows live supply, placement, bolt-gantry, and NG-transfer
positions over an operator-oriented top view. The fixed alignment camera, moving
inspection optics, one main carrier conveyor, NG Conveyor, and NG Shuttle are shown
as distinct mechanisms. The Settings screen contains the complete mapped I/O and
axis view.

The map does not infer material from a command or SMEMA alone. PCB is green, housing
is amber, and carrier jigs are blue. The two upstream PCB slots remain marked unknown
until the supply pickup sensor checks them. A pneumatic feedback timeout raises an
alarm on the supply or placement handler that issued the command.

One MOVS light controller drives two independent lights. Channel 1 is used for
fiducial capture and channel 2 for inspection by default; each channel has its own level.
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
