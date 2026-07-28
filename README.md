# IBTM

IBTM is a .NET 10 WPF machine-control application for a three-station carrier-jig line.
The stations work independently while sharing one servo conveyor.

## Process

1. `PcbSupply` receives a two-PCB carrier, picks one PCB, keeps it in the supply
   gripper, and rotates the handler to the handoff position. This handler has only
   X and Z motion axes.
2. `PcbPlacement` grips the PCB directly from the rotated supply handler. The supply
   gripper releases only after the placement gripper confirms pickup. The placement
   handler moves to the first fiducial position before allowing the supply handler
   to return home. It then captures both fiducials, averages the X/Y corrections,
   and places the PCB in its housing.
3. `BoltFastening` moves one shared XYZ mechanism and selects either the standard
   blow-fed head or the separately supplied Loctite head for each bolt point.
4. `Inspection` captures both PCB positions and either sends the carrier jig
   downstream or moves the complete carrier jig to the NG stack.

The conveyor moves only one carrier jig at a time. A carrier jig lifted by a station
backup plate is mechanically separated from the conveyor, so another carrier jig can
move between other stations.

The supply and placement grippers each have a dedicated PCB-presence input in
addition to their open/closed feedback. A missing PCB at the supply handler skips
that handoff. A missing PCB at the placement handler skips alignment and placement;
fiducial failure with a detected PCB is an alarm.

## Projects

```text
IBTM/                               WPF host, settings, persistence, DI, UI
IBTM.Core/                          Shared coordinates, process events, results
IBTM.Device/                        Hardware boundaries, motion safety, maps, lighting
IBTM.Ajin/                          AJIN motion, I/O, and conveyor implementations
IBTM.Hik/                           Hik area-camera implementation
IBTM.Transport/                     Conveyor and carrier-jig positioning
IBTM.PcbSupply/                     Upstream PCB carrier, rotation, and direct handoff
IBTM.Stations.PcbPlacement/         PCB pickup, two-point XY alignment, placement
IBTM.Stations.BoltFastening/        Bolt pattern execution
IBTM.Stations.Inspection/           Inspection, downstream transfer, NG stacking
IBTM.Sequence/                      Concurrent station loops and machine lifecycle
IBTM.Virtual/                       Virtual hardware
IBTM.Virtual.Tests/                 Virtual machine and sequence tests
```

Station projects do not reference one another. `IBTM.Sequence` coordinates their
process flow, and `IBTM.Transport` owns the shared conveyor behavior.

## Teaching model

Each mechanism owns its directly taught positions. PCB placement stores both housing
positions, bolt fastening stores both PCB references plus a shared relative bolt
pattern, and inspection stores its own capture positions. No shared calibration or
cross-station coordinate conversion is applied implicitly.

Alignment-camera positions are separate machine motions:

```text
Pick PCB
  -> rotate supply handler
  -> direct gripper-to-gripper handoff
  -> Fiducial 1 capture
  -> Fiducial 2 capture
  -> average X/Y correction
  -> taught PCB place position + correction
```

The alignment camera does not calculate PCB rotation or scale.

## Hardware configuration

Logical `InputIo`, `OutputIo`, and `MachineAxis` values map to physical channel
numbers in `MachineSettings.json`. Hardware and camera drivers are selected
independently:

- `Driver`: `Virtual` or `Ajin`
- `CameraDriver`: `Virtual` or `Hik`

There are two cameras: `AlignmentCamera` and `InspectionCamera`.

`Hardware.OutputFeedbacks` pairs every pneumatic output with the input state that
confirms its ON and OFF motion. Each pair has its own timeout in milliseconds.
Grippers, stoppers, backup plates, and the supply rotation use this map. SMEMA,
lasers, tower lamps, and the buzzer have no direct actuator feedback and are not
included. A feedback timeout marks the active process stage as an alarm and stops
the automatic sequence.

The bolt-fastening mechanism has one shared motion and two heads mounted at fixed
X/Y offsets. Each head has its own tightening controller and feeder controller.
`StandardHead` uses blow feeding; `LoctiteHead` uses a separate non-shooting supply.
Recipes select `Standard` or `Loctite` for every bolt point. The application currently
uses the virtual bolt-head implementation; vendor controller and feeder drivers are
added when their protocols are selected.

Every automatic horizontal move follows the same motion order:
`SafeZ -> X/XY -> work Z -> SafeZ`. `SafeZ` is configured for each mechanism.
Manual horizontal jog is enabled only while that mechanism is at `SafeZ`.
`MotionService` enforces this below the stations and UI. AJIN and Virtual expose
their raw XY commands only as protected implementation methods.

The process monitor shows live supply and placement handler positions over the
machine's top-view layout. The shared conveyor and carrier jig are shown on the
lower lane, and key PCB, gripper, housing, camera, and conveyor states change with
the machine. The Settings screen contains the complete mapped I/O and axis view.

One MOVS light controller drives two independent lights. Channel 1 is used for
alignment and channel 2 for inspection by default; each channel has its own level.
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
