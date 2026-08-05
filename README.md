# IBTM

IBTM is a .NET 10 WPF machine-control application for a three-station carrier-jig line.
The stations work independently while sharing one servo conveyor.

## Machine layout

1. PCB Pickup Transfer has X/Z motion, pneumatic grippers, and pneumatic rotation.
2. PCB Handler & Place has XYZ motion and moves the PCB over a fixed alignment camera.
3. Bolt fastening has one shared XYZ motion group and two fastening heads.
4. The inspection optics move with the NG XY Transfer. NG Conveyor and NG Shuttle are
   separate from the main production conveyor.

The conveyor moves only one carrier jig at a time. A carrier jig lifted by a station
backup plate is mechanically separated from the conveyor, so another carrier jig can
move between other stations.

The CAD-derived mechanical grouping and its UI mapping are documented in
[Machine Layout](docs/MACHINE_LAYOUT.md).

The supply and placement grippers each have a dedicated PCB-presence input in
addition to their open/closed feedback. A missing PCB at supply position 1 advances
the handler to position 2. A missing PCB at position 2 completes the upstream
carrier without rotation or buffer transfer. A missing PCB at the placement handler skips
alignment and placement; fiducial failure with a detected PCB is an alarm.

## Projects

```text
IBTM/                               WPF host, settings, persistence, DI, UI
IBTM.Core/                          Shared positions, images, enums, and results
IBTM.Device/                        Hardware boundaries, motion safety, maps, conveyor, lighting
IBTM.Ajin/                          AJIN motion, I/O, and conveyor implementations
IBTM.Hik/                           Hik area-camera implementation
IBTM.PcbBuffer/                     Shared PCB buffer ownership and collision lock
IBTM.PcbSupply/                     Supply handler state, settings, and recipe
IBTM.Stations.PcbPlacement/         Placement hardware, settings, and recipe
IBTM.Stations.BoltFastening/        Fastening hardware, settings, and recipe
IBTM.Stations.Inspection/           Inspection hardware, settings, and recipe
IBTM.Virtual/                       Virtual hardware
IBTM.Virtual.Tests/                 Critical virtual safety checks
```

Feature projects may read the shared `IIoService`, but each output is commanded
only by the component that owns the physical device. Supply, placement, fastening,
inspection, and conveyor operations therefore go through their owning classes.
The process monitor reads all I/O for display, and the manual Digital Outputs
window is the intentional maintenance bypass.

Supply and placement do not reference each other or the Buffer project. The WPF
host's `PcbBufferService` owns Buffer locking, cancellation, recovery, and verified
release while each handler owns only its motion and IO steps.

Station motion has no global execution lock. PCB supply and placement share only
the Buffer collision lock; bolt fastening and inspection remain runnable while a
Buffer transfer is active. Conveyor ownership stays in the host because the three
carrier-jig stations share one physical servo conveyor.

Supply may receive, pick, and rotate the next PCB while the Buffer is occupied or
Placement is working. It waits while holding that PCB and acquires Buffer ownership
only for the actual placement transaction.

There is no global sequence project. Automatic station transitions are intentionally
not implemented yet. The WPF host initializes the equipment and handles stop,
emergency stop, reset, and safety-input changes.

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
positions, bolt fastening stores both PCB references plus a shared relative bolt
pattern, and inspection stores its own capture positions. No shared calibration or
cross-station coordinate conversion is applied implicitly.

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

Logical `InputIo`, `OutputIo`, and `MachineAxis` values map to physical channel
numbers in `MachineSettings.json`. Each motion axis also has an editable logical
minimum and maximum used by target motion and the live equipment map. AJIN motion defaults to
0.01 millimeters per pulse. AJIN and Virtual use the same editable pulse length
when converting motion positions and speeds. Hardware and camera drivers are
selected independently:

- `ControlDriver`: `Virtual` or `Ajin`
- `CameraDriver`: `Virtual` or `Hik`

There are two cameras: `AlignmentCamera` and `InspectionCamera`.

`Hardware.OutputFeedbacks` pairs every pneumatic output with the input state that
confirms its ON and OFF motion. Each pair has its own timeout in milliseconds.
Grippers, stoppers, backup plates, and the supply rotation use this map. SMEMA,
lasers, tower lamps, and the buzzer have no direct actuator feedback and are not
included. A feedback timeout fails the caller that commanded the output.

The bolt-fastening motion group has one shared XYZ motion and two heads mounted at fixed
X/Y offsets. Head 1 is the standard blow-fed head. Head 2 is the Loctite head and uses
its own vacuum pump and vacuum sensor when picking from the taught ZEDA KS1069C-S
pickup index. Each head has its own ADC-400 tightening controller. Feeder control and
feedback wiring remain pending. Recipes select `Standard` or `Loctite` for every bolt
point.

The ADC-400 serial protocol and maintenance test window are implemented. The
production `IBoltHead` adapter is not connected yet, so bolt-head operation remains
Virtual while the actual preset/result execution contract is being defined.

Every automatic horizontal move follows the same motion order:
`SafeZ -> X/XY -> work Z -> SafeZ`. `SafeZ` is configured for each motion group.
Manual horizontal jog is enabled only while that motion group is at `SafeZ`.
`MotionService` enforces this below the stations and UI. AJIN and Virtual expose
their raw XY commands only as protected implementation methods.

`MoveZToPositiveLimitAsync` is the homing/recovery exception for a Supply Z axis.
AJIN uses
`AxmMoveSignalSearch` to stop on the logical positive limit; Virtual moves to the
configured Z maximum and raises the same limit state. Reaching this limit does not
mark the axis as homed. Supply initialization confirms the rotated input, moves Z
to this lower limit, homes X toward the outside of the machine, and finally homes Z
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
