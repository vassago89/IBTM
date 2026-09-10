# IBTM

.NET 10 WPF control application for a three-station carrier line.
One executable hosts independently enabled machine units and the teaching UI.

## Project folders

- `IBTM/`: WPF application and machine coordination. The startup project stays here.
- `Hardware/`: AJIN, AlphaMotion, HANTAS, Hik and virtual device implementations.
- `Stations/`: Conveyor, PCB handling, fastening, inspection and training projects.
- `Shared/`: Core types, device contracts and storage.
- `Tests/`: Driver stand-ins and virtual regression tests.

The solution uses the same folders. Project names, assemblies and namespaces are
unchanged; folder grouping does not add another layer of code or control flow.

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
| `IBTM.Ajin.Tests` | AJIN DIO discovery, 16/32-point scanning and mapping tests; no native calls |
| `IBTM.AlphaMotion` | AlphaMotion PCIe IO |
| `IBTM.AlphaMotion.Tests` | TMC-AE16DIOe driver tests against a test-only SDK stand-in; no native calls |
| `IBTM.Hantas` | Shared ADC serial bus and individually addressed bolt heads |
| `IBTM.Hik` | Hik area camera |
| `IBTM.Virtual` | Virtual devices and optional material-flow scenario |
| `IBTM.Virtual.Tests` | Motion/IO boundaries, stop/resume and machine-flow regression tests |

The host composes the projects. Hardware-owning objects read their own IO and
expose feedback and operations; automatic units use these objects, not arbitrary
IO numbers. The Operation page displays unit feedback. Digital Inputs/Outputs
windows are the intentional maintenance access.

The sidebar's **LOGS** button opens one shared log window, with the newest
messages at the top. Initialization, physical DI changes/DO commands, alarms,
ADC frames, Trace messages and unhandled exceptions are recorded even when
the window is closed. Errors include their exception details and stack traces.
The window keeps recent messages; complete session logs are written under
`Logs/IBTM-<timestamp>-<process>.log` beside the executable. **Pause display**
and **Clear view** only affect the display, not collection or file recording.
If file recording fails, the window reports the failure and retains recent
messages in memory. Successful 10 ms input polls are not logged individually.

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
and method use the existing AJIN axis settings; startup uses `AxlOpenNoReset`
without loading a `.mot` file. The driver still sets pulse units and acceleration
units explicitly (it does not preserve every axis parameter unchanged), and uses pulse units
internally and converts acceleration time to pulses/s². Virtual currently models
travel speed and pulse resolution, not the AJIN acceleration or home-search profile.

### Repeat / Dry Run (Auto 기반)

Operation의 `REPEAT (DRY RUN)`을 켜면 기존 Auto 정방향을 실행한 뒤 캐리어 하나를 복귀시킨다.

Station 1 → Station 2 → Station 3 → NG Transfer → 셔틀 → NG 끝단(P1)
→ 셔틀 → NG Transfer → Station 3 → 메인 앞 센서 → Station 1 → 반복

- Manual에서 Main Conveyor, NG Carrier Transfer, NG Shuttle, NG Conveyor를 ON으로 설정한다.
- 공정 없이 순환만 확인하려면 PCB Supply, PCB Placement(1번), Bolt Fastening(2번),
  Inspection(3번), 두 Bolt Feeder를 OFF로 둔다.
- NG Transfer의 Station 3 픽업 / 셔틀 놓기 위치를 티칭하고, 캐리어를 넣기 전에 HOME ALL을 완료한다.
- 첫 번째 백업 플레이트 구간에 캐리어 한 개를 놓고 `REPEAT (DRY RUN)`을 체크한다.
- AUTO 스위치로 전환하고 기존 START를 누른다. STOP으로 정지한다.
- 미사용 스테이션도 캐리어 감지와 백업 플레이트 UP을 확인한 뒤 완료/통과한다.
- 정방향은 별도 시험 시퀀스가 아닌 Auto다. 켜둔 공정은 실제 작업을 수행하므로,
  체결하지 않을 시험에서는 Bolt Fastening을 OFF로 둔다. 가짜 체결/검사 결과를 만들지 않는다.
- Repeat 중에는 외부 캐리어를 추가 요청하거나 후방 SMEMA로 배출하지 않는다.
  Repeat를 끈 일반 Auto에서는 NG Transfer OFF일 때 기존 후방 Ready/Available 핸드셰이크로 배출한다.
- NG 끝단 도착 후 모든 정방향 작업의 취소/정지 완료를 기다린 다음 역이송한다.
  NG 역이송은 셔틀 DOWN, XY 이송은 픽업 UP, 메인 복귀는 헤드 간섭 조건을 확인한다.
- STOP 후에는 미완료 복귀 방향을 유지하고 현재 IO로 이어간다.
  센서 사이에 정지하여 위치를 확인할 수 없다면 임의로 움직이지 않고 위치 확인을 요구한다.
  프로그램 재시작은 복귀 이력을 보존하지 않으므로 첫 번째 플레이트 또는 확인 가능한 구간에 다시 준비한다.
- Cycles는 Station 1 복귀·안착 완료 횟수다. Repeat 선택과 횟수는 실행 세션 동안만 유지한다.

예전 Manual의 개별 Dry Run 선택 및 별도 PCB/검사/체결 포인트 왕복 시퀀스는 제거했다.
이 모드는 요청한 전체 NG 순환이며, PCB를 Supply로 회수하는 별도 시험 모드는 포함하지 않는다.

See [machine layout](docs/MACHINE_LAYOUT.md),
[Supply behavior](Stations/IBTM.PcbSupply/DESIGN.md) and
[Buffer handoff](Stations/IBTM.PcbBuffer/DESIGN.md) for detailed mechanical contracts.

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
Only enabled motion groups participate in hardware readiness and homing. Supply
and Placement are initialized independently; their shared buffer handoff still
requires both handlers' valid positions.

During automatic operation the shared background worker samples enabled motion
feedback at least once per 250 ms wait interval, even with the MOTION window closed.
An observed axis fault, lost servo/home readiness or failed feedback scan latches
the corresponding alarm and cancels automatic operation. Recovering feedback does
not clear that alarm or restart the machine. Each physical/virtual motion object's
monitor starts during machine initialization and refreshes its cached position and
signals every 250 ms, including while idle. Motion-completion waits remain separate
and return when their command finishes.

State lookup belongs to `MachineState.GetMotionStatus`; the UI and command controller
share that registry. `MotionStatus.MonitorAxes` and `RefreshMonitorFeedback` are the
independent raw monitor path. `Axes` and `RefreshControlFeedback` are the
enabled/initialized control-display path; commands still recheck live feedback.
`MachineController.Display.cs` assembles UI snapshots, while
OUTPUTS calls `MachineController.ToggleDiagnosticOutput` directly through
`OutputWindowRow.ToggleCommand`. It reads the current DO value and writes its
opposite, without a feedback wait or asynchronous operation scope.
`OutputControlRow` remains the separate coordinated Manual Control path;
teaching uses `ManualSetupEnabled` and `RunTeachingOutputAsync`.
`MainConveyorPathClear` means collision clearance, not whole-machine readiness.
Rejections and hardware errors are logged and shown on the row without clearing
the existing machine alarm.

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
the existing cancellation path stops homing.

OUTPUTS is direct manual I/O control. Every mapped output uses **ON / OFF**,
including stoppers, shuttle, shooting, conveyors and interface signals. Unit
Enable, homing, servo state, latched alarms, busy status, carrier/peer sensors and
handler positions do not gate these toggles. DI feedback is displayed only;
missing or conflicting feedback does not delay another click.
RUN changes the RUN output only: it does not select direction/speed, assert other
outputs or start a transfer sequence. Paired valve outputs still use the configured
complementary ON/OFF channel mapping.

The window is MANUAL-only. Each command rechecks MANUAL mode, emergency-stop
release, available I/O and application shutdown. Machine STOP and the existing
safety-stop path remain active. AUTO selection or closing OUTPUTS stops conveyor,
feeder, shooting and interface run outputs; it does not reset alarms or reverse
pneumatic valves. OFF does not close the window, and a missing display snapshot
does not prevent opening it.

Manual Control's conveyor Run/Stop rows use the same minimum manual/I/O/E-STOP
conditions, without unit-enable, carrier, peer-handshake or unrelated-motion
gates. RUN selects forward/normal speed and owns its cancellation lifetime;
STOP can also stop a motor started from OUTPUTS. Automatic and Dry Run sequences
retain their own route, clearance and readiness checks.

Setup editing and teaching lists are independent of unit Enable, servo, homing
and latched alarms. Pages remain viewable while another manual operation runs;
data writes still require idle MANUAL so running operations do not read changing
settings. Teaching the current readable position does not move an axis and does
not require Servo ON. It rejects unavailable position feedback.
Manual moves check only the selected mechanism's initialized, homed, servo-on,
fault-free axes and its actual clearance, not unrelated axes or latched alarms.
Individual Home likewise checks its selected axes and existing Home clearance;
HOME ALL retains its all-enabled-axis readiness checks.

Teaching cylinder/vacuum commands do not require unrelated handler readiness.
Supply rotation still requires its own motion because it first moves Z to the
rotation height; placement rotation keeps its physical clearance check. Feedback
waits and cancellation remain on these coordinated teaching commands.

Camera live view does not require motion readiness or an alarm reset; AUTO or
leaving the view stops it. Reinspection of a saved image uses the data-edit path,
not the motion path. Moving camera scans retain inspection-axis requirements.
ADC head tests no longer require unrelated axes to be homed/ready. The ADC window
can remain open to view logs when its hardware commands are blocked.
Unrelated alarms no longer cancel a lighting test, and a failed light-OFF command
can be retried even while another operation is busy.

The sidebar **MOTION** button opens a separate Motion Monitor that stays open in
AUTO, during motion and during alarms. It groups axes by mechanism and shows the
active controller axis number, position, servo, homed/home sensor, limits and alarm
feedback. It only displays each motion object's background monitor cache; it has no
polling timer and does not own native reads. All configured axes, including disabled
axes, are sampled without enabling servos or initializing motion parameters. Closing
the window does not stop these object-owned loops; machine shutdown cancels and drains
them. Search accepts the axis number or name; enabled axes are shown by default.
Disabled axes remain read-only but display their actual feedback. An unreadable state
or position is shown as unknown independently; one axis/group failure does not hide
healthy monitor rows. Disabled-axis diagnostics do not enter machine readiness/alarms.
AJIN startup no longer turns servos ON automatically. Axis alarms and Servo OFF
are displayed as feedback, not as missing communication; failed Servo ON/RESET
commands do not hide readable positions or signals. Explicit Servo controls and
the existing RESET sequence remain separate from startup initialization.
Servo and individual Home buttons reuse the existing controller interlocks;
PCB Supply still uses coordinated HOME ALL. Closing this window cancels only homing
started there; its STOP button stops the machine. Jog/teaching remains on the existing pages.
Automatic, saved-position and Home X/Y moves require raised-cylinder feedback
from its unit: Placement Handler, Fastening Head 1/Head 2, or NG Pickup.
Up must be ON and Down must be OFF. Loss of this condition during X/Y movement
alarms the unit and cancels the machine's operations. Z-only movement is separate;
the condition is checked again before the following X/Y command starts.
Placement enters buffer X/Y with its Handler raised and IPM lowered for pickup.
It keeps IPM Down while carrying the PCB.
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

The mode selector input is **ON = MANUAL, OFF = AUTO**. Digital Inputs shows the
raw contact value as `Auto / Manual Selector`, with an ON/OFF mode legend; the persisted `AutoMode` input
mapping key and its channel are unchanged. Virtual control starts with the same
MANUAL (ON) contact state. Mode changes do not start automatic operation.
The header uses an amber MANUAL badge and a green AUTO badge; unavailable status
shows a neutral UNKNOWN badge instead of implying MANUAL.

All six door contacts are **ON = CLOSED, OFF = OPEN**. Digital Inputs and I/O
settings label them `Door 1 Closed` through `Door 6 Closed`, retaining the existing
`Door1Open`–`Door6Open` database keys and channel mappings. All six inputs must be
ON for the door-closed indicator/interlock; loss of a closed contact is treated as
open. Virtual doors start closed (ON). Closing a door does not clear its latched
alarm or restart automatic operation.

`Enabled Units` also selects motion initialization, status polling, readiness/alarm
checks, RESET, HOME and manual servo commands. PCB Supply and PCB Placement are
independent; the inspection gantry is required if either Inspection or NG Carrier
Transfer is enabled. Disable unused units in MANUAL, save, then RESET to clear an
existing motion alarm. Shared-buffer automatic transfers still require both
handlers enabled and homed. Emergency-stop, door and raised-cylinder interlocks
remain active where required, even when an adjacent unit is disabled.

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
AlphaMotion is the **TMC-AE16DIOe** (16 DI / 16 DO), using the manufacturer's
A-series `TMCAEDLL.AIO_*` functions, not Motionnet `nmiMNApi` or B-series `AIO_pmi*`.
Settings exposes only **Card No.**, matching the Digital IO utility (normally 0).
The persisted `ControllerNumber` is retained; old Station/CommunicationSpeed fields
are ignored when loading old settings. Startup uses the sample's `AIO_BoardInfo`
to discover actual DI/DO counts, then probes each available direction before readiness.
The board does not need exactly 16 DI / 16 DO; each mapped channel is checked against
its direction's reported count. The logical AlphaMotion window remains 0–15 regardless
of board size, so AJIN addresses never shift. Model and
communication codes are logged for diagnosis, not used as a model whitelist;
the field-observed model `0xAE2E` is accepted when the required I/O checks pass.
Input/output reads use the sample's `AIO_GetDIDWord` / `AIO_GetDODWord`, group 0;
only the mapped channels 0–15 are exposed to the application. Larger boards' extra
channels are not automatically added to the machine mapping.
The manufacturer's C# sample (`frmDIGITAL.LoadDevice`) treats `AIO_LoadDevice()`
as a board-count result: negative means failure, while a nonnegative result plus
one is the loaded board count (`0` means one board).
The remaining return conventions are **not yet verified against the equipment's
DLL**. For user-operated field testing, results 0 or 1 are candidate successes
only with `AIO_GetErrorCode() == ERR_SUCCESS`; negative/unknown results and SDK
errors still throw. Read buffers start at `0xFFFFFFFF`: missing/invalid DI/DO counts
and port bits outside the reported channel range are rejected, not masked into OFF.
For a full 32-bit port, all-ON data is distinguished from an untouched buffer by
repeating the read with a changed seed (at most three reads, allowing an ON-to-OFF
transition). A direction with zero channels is not queried; accessing a mapped channel
in that direction still fails. Every output command requires matching port readback (not confirmation
of physical actuator movement). Unload result 0 with no SDK error no longer
causes a secondary cleanup exception; the controller stays unavailable on close.
Logs include raw native results, error codes and returned board/port data on the
first call and newly observed status combinations, without logging every poll.
The zero-result path is labeled as requiring hardware verification. Compare
sensor ON/OFF transitions with the manufacturer's monitor before automatic operation.
Use the manufacturer's matching `tmcDApiAed_x64.dll` and installed board driver
with a 64-bit process. Place that DLL in `Hardware/IBTM.AlphaMotion/` to have builds and
publishing copy it beside the executable, or deploy it there directly. The DLL is
not supplied by the C# declarations. Initialization does not issue reset,
filter-setting or output-write commands.
AJIN currently uses `AxlOpenNoReset` for user-operated field testing, without
loading a `.mot` file or falling back to `AxlOpen` if opening fails. The raw open
result is logged. The saved `MotionParameterFile` value is retained for compatibility
but is ignored and disabled in the settings UI. Existing home/signal settings must
already be valid; preservation across a power cycle has not been verified.
Motion startup still applies the application's pulse scaling and acceleration units
and enables the configured servos; homing still applies the configured speeds.
Motion coordinates exposed to units are
millimetres, converted from pulses using the configured millimetres-per-pulse.
AJIN then validates and logs each configured DIO module's identity and DI/DO
counts. Input scans use WORD offsets 0/1 for 32 DI and only offset 0 for 16 DI,
following the manufacturer's DigitalIO sample. The existing 32-bit address slots
are preserved; bits 16..31 of a 16-point module are invalid. Missing modules,
wrong input/output directions and unsupported point counts prevent readiness.

If control I/O initialization or the connection fails, the display retains the
original communication error and leaves output feedback unavailable. Display and
idle-state queries do not read outputs from unopened hardware. After correcting
the underlying driver/connection error, RESET retries hardware initialization.
The main-window footer also provides RESET on every page, including Settings;
it calls the same recovery path as the physical reset input and may enable servos,
but does not home axes or start automatic operation. Existing reset safety/busy
checks remain in force. Settings require stopped MANUAL mode, including during
an alarm. A configuration fault can be corrected before resetting after switching
to MANUAL. AUTO, active operations and shutdown lock settings;
motion, output and teaching permissions are unchanged. The Settings page explains
the current edit lock. Driver/connection changes still require saving and restart.

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

Digital Inputs supports text/number search and a unit filter. Input/output windows
show the configured IO numbers beside each signal. Filtering only changes the
visible rows; all inputs continue updating. A disconnected input service displays
UNKNOWN (`—`) instead of stale ON/OFF values, and reconnecting refreshes all rows.

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
separate from Repeat. Disable Bolt Fastening when circulating without fastening.
With a Virtual ADC, the window also stays available during Auto for **Next Result**:
select the slave, choose OK/NG/Error and click **Apply Once**. The next fastening
start on that controller consumes the result; an already running fastening is
unchanged. Other protocol commands retain their Manual/safety interlocks.
An injected Error remains active across preset/direction changes until ADC Alarm
Reset; attempts to Start during the error do not consume a queued result.
Lighting has its own Virtual/MOVS driver selection, independent from motion/I/O.
Settings → Devices & Safety → Lighting owns its COM port, baud rate, data bits,
parity, stop bits, write timeout and inspection channel. Driver and serial changes
require save/restart. Existing databases retain their previous light selection and
COM port; a blank or failed MOVS connection is reported by hardware initialization,
not dependency construction. Virtual development also forces the light driver to Virtual.
The MOVS light controller defaults to 19200 baud, 8-N-1 and a 1000 ms write timeout, with the existing
`:L{channel}{level:000}\r\n`, `:O{channel}\r\n`, `:F{channel}\r\n` commands.
The **Light Test** controls in that settings card use the active driver/connection,
with a separate single-channel selection and temporary 0–255 brightness (default 80).
ON/Test requires idle MANUAL, holds an operation until OFF and does not save recipe
brightness. OFF, machine STOP, switching to AUTO, leaving Settings or shutdown ends
the test and sends OFF to the captured test channel. Initialization, ON/OFF writes
and failures are logged; these are command results, not hardware readback. Failed
OFF writes explicitly report an unknown light state and retain the original channel.
The OFF button can retry that channel without sending brightness or ON commands,
even if the channel field was edited. New ON tests remain blocked until OFF succeeds.
An OFF-only retry requires the machine idle and is also available in AUTO; shutdown
makes one final cleanup attempt. Serial edits still need save/restart.

## Build and validation

For UI/layout/text-only changes, skip tests and compile only when needed. For
control logic, run only the directly affected safety/regression tests in one configuration.
`dotnet test` already builds its dependencies; do not repeat a solution build or
the same tests in Debug and Release for every edit. Manufacturer SDK stand-in tests
are fast. The remaining long route simulations have `Category=MachineFlow` and
are excluded by default in `IBTM.Virtual.Tests`. An explicit filter overrides
that default. Run only the affected tests during development.

`MachineLifecycleTests` is split into Repeat, Transfers, Teaching, Motion, Display, Flow
and Support partial files. It remains one xUnit class, so splitting the source
does not introduce concurrent machine scenarios or a new fixture hierarchy.

```powershell
# Choose the command relevant to the change, rather than running all three.
dotnet test Tests/IBTM.Ajin.Tests/IBTM.Ajin.Tests.csproj -c Virtual --no-restore
dotnet test Tests/IBTM.AlphaMotion.Tests/IBTM.AlphaMotion.Tests.csproj -c Virtual --no-restore
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~AlarmRecoveryTests"
```

Long machine-flow checks, only when requested:

```powershell
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "Category=MachineFlow"
# Explicitly include every virtual test only for a requested full verification.
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~IBTM.Virtual.Tests"
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
[training guide](Stations/IBTM.Inspection.Training/README.md).

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
