# UI execution and exception review

Reviewed: 2026-09-06. This is a call-path review, not a count of guards or catches.

Status: this review pass is closed; recurring review is paused. Reopen for a new
failure, a changed call path, or the outstanding evidence below. This is not
physical-equipment acceptance.

## Review rule

For each guard, record: entry point -> execution context -> first yielding or
reentrant call -> shared state/resource -> existing protection -> decision.
Do not add protection unless the actual call path explains what it prevents.

Review one operation end to end, not every file independently:

1. Find every caller: bound UI command, direct call, physical input, worker.
2. Identify where execution can yield or reenter. Follow callbacks as well as awaits.
3. Name the condition or resource being protected and its existing owner.
4. Decide: remove a duplicate, simplify its implementation, retain a real boundary,
   or defer an unverified behavior. Record the evidence, not a hypothetical race.
5. After an edit, follow its callers and cleanup once more. Close the branch when
   ownership is clear and no connected failure remains; do not restart the audit
   merely because another layer has a try/finally.

Execution contexts:

| Context | What can overlap? | Normal treatment |
| --- | --- | --- |
| UI synchronous work | No other UI input unless the code pumps messages or synchronously raises a reentrant callback | Use ordinary statement order; no thread lock |
| UI async command | Other commands/events can run while an awaited task is incomplete; an already-completed await does not yield | Existing command eligibility or local page exclusion |
| Modal UI / synchronous callbacks | ShowDialog pumps messages; operations such as releasing mouse capture can call handlers again | Keep the specific reentry state, not a general thread lock |
| Background work | Task.Run, motion/IO monitors and camera SDK callbacks can overlap UI or each other | Protect only shared state; marshal UI updates |
| Shared device operation | Serial bus, camera stream and machine operation lifetimes can outlive their UI caller | Device/operation owner handles serialization, cancellation and cleanup |

AsyncRelayCommand's default CanExecute disables a bound UI command while it runs.
It is not a machine-wide lock, does not exclude a different command, and should
not be treated as an admission check for arbitrary direct ExecuteAsync callers.

## Keep these responsibilities separate

1. UI eligibility: prevent invalid clicks/repeated editing. Prefer bindings and
   the existing command's running state. Do not duplicate it with a busy flag.
2. Operation admission: decide whether a NEW operation may start. Check before
   claiming its lifetime. A running operation must not reject itself as busy.
3. Continued operation: current safety, mode and hardware conditions. Becoming
   busy is normal after admission, not a cancellation reason by itself.
4. Resource ownership: the owner releases its operation/device resources. The UI
   should not own a second competing copy of that responsibility.

UI admission does not replace device safety: automatic runs, physical buttons
and background faults do not all originate on the WPF dispatcher.

## Current decisions

| Location | Actual execution / purpose | Decision |
| --- | --- | --- |
| AdcProtocolWindow.ExecuteCoreAsync -> MachineController.RunAdcProtocolAsync -> TestBoltHeadAsync | The window previously registered an operation before the controller's second idle check | **Fixed**: RunAdcProtocolAsync admits and owns one operation; the internal fastening step checks readiness without registering another operation |
| AdcProtocolWindow.RefreshControls / CanUseAdcProtocol | A serial request awaits a reply; its own scope previously canceled/disabled it | **Fixed**: CanUseAdcProtocol checks idle admission; AdcProtocolAvailable checks mode/safety without rejecting the operation's own activity. The controller handles continued-operation cancellation; RefreshControls only displays state |
| InputWindow display | `IoSignals` owns the shared read-only rows; GetInput reads the device's input image, not an SDK call per binding | **Simplified**: InputWindow no longer subscribes to DI or copies state. Input, Output and teaching use the same rows; only Virtual Auto Response has a window-owned subscription |
| CommandShutdown.WaitAsync second OCE catch | Covered faulted OCE tasks fabricated by an ignored test; current async command paths cancel on OCE | **Removed** with its fabricated test. Ordinary canceled-task handling and real failure propagation remain |
| OutputControlRow.ToggleAsync nested try | One UI command, synchronous output then async feedback wait | **Simplified** to one try/catch/finally; timeout display, canceled-command handling and waiting cleanup are retained |
| OutputWindow feedback updates | Shared `IoOutputStatus` observes its mapped input rows and publishes match changes | **Simplified**: no window IO subscriptions, state copies or all-row scan. OutputControlRow uses WPF's weak property-change subscription for its derived command result; waiting/timeout remain command-owned |
| StationTeachingViewModel live-image error callback | Error display runs on UI, but frame handoff belongs to a worker | **Simplified** UI-only try/finally nesting. SDK error display, shared-frame lock, and device-owned light cleanup remain |
| MainWindow / OutputWindow asynchronous close flags | await allows another Close; completion deliberately calls Close again | **Keep**: closing and completed represent different phases |
| HoldButton._holding | ReleaseMouseCapture can synchronously raise LostMouseCapture | **Keep**: prevents repeated release/Stop on one UI thread |
| TeachingMotionViewModel cancellation catch | User Stop/deactivation cancels a linked motion awaited by the UI command | **Keep** normal cancellation handling; actual motion failure must remain observable |
| Camera live-image gate and display Interlocked flags | SDK/IO/motion sources are background callbacks | **Keep** shared-frame protection and event coalescing; these are not UI-only locks |
| OperationCancellation gates / drain | UI Stop, worker completion and cancellation callbacks share lifetime state | **Keep** actual cross-thread ownership; audit its use as a GUI permission separately |
| Shutdown cleanup finally / captured pending tasks | Cancellation invokes hardware callbacks; async cleanup must finish before DI disposal | **Keep** cleanup ownership; do not rerun already-completed historical failures during close |
| NgCarrierConveyor.StartConveyor / RunUntilAsync / RunAsync | An IO callback can cancel during motor setup; cancellation and sensor-wait completion can run outside the UI thread | **Keep** the cancellation check before RUN, token-owned immediate Stop, and finally-owned Stop/unsubscription. These cover different boundaries, not duplicate UI click guards |
| Settings.AlphaMotion -> AlphaMotionController | UI editing changed Controller/Station numbers used by the running IO monitor and shutdown, despite the restart-required notice | **Fixed**: the controller retains its constructor-selected address and communication speed. New settings apply to a new controller; no lock, busy flag or exception wrapper added |
| Settings.InspectionCamera -> HikCamera | Exposure/Gain were only sent at initial connection; later captures and live starts silently used old values | **Fixed**: the camera applies current values before a single trigger and before starting live acquisition, within its existing grab lock. No new settings cache or UI device-control layer |

## Exception policy

- Catch where the caller can do something: show a file/protocol error, set the
  owning unit's alarm, or recognize user cancellation.
- Do not catch-and-rethrow without a necessary state/resource change.
- Keep finally for an actual resource/output/subscription obligation, not merely
  because a sequence has several UI assignments.
- Do not turn programmer errors into ordinary machine status or silent success.
- A long try/finally is not evidence of redundant safety; follow what it releases.

## Verification evidence

These checks passed during the relevant edits, not as a full-equipment acceptance
test. Assertion counts describe the existing runners, not new test cases.

| Edited path | Focused verification |
| --- | --- |
| ADC admission and lifetime | Existing ADC WPF runner: 49 assertions; focused operation-lifetime test. Delayed replies, local/global Stop, close, mode/safety and recovery |
| Input/Output windows | Existing --io-only runner: 8 assertions. Live values, feedback states, timeout and retained pneumatic output after cancellation |
| Shutdown | Existing --shutdown runner: 15 assertions. Cancellation, actual failures, pending capture and modal close |
| Live camera error cleanup | Existing --live-only runner: 2 assertions. Conversion failure and restart, without training a model |
| Shared OperationViewModel shutdown helper | Release build; same capture-before-cancel and finally-await order retained |
| AlphaMotion connection identity | Actual controller source compiled against an in-memory SDK stand-in: before-fix reads changed from 0/1 to 7/9; after-fix input, output, close and reinitialization kept 0/1. A new instance used 7/9 and the edited speed. No native SDK or equipment calls |
| HIK exposure/gain | Built HikCamera exercised with managed proxies for the existing vendor interfaces. Before-fix neither path sent edited values; after-fix both sent them before the trigger/live-start boundary. Native acquisition and resulting brightness still require the actual camera |

Release builds for these edits had no warnings or errors. Harness corrections
(awaiting actual Reset/Closed completion and including shared WPF resources) were
kept in the harness; no product delays or guards were added to satisfy them.

## Reviewed without a further change

- Supply/Station teaching, camera capture and actuator commands: UI entry checks
  idle eligibility; inner handlers check mechanical/device conditions. Their
  operation scopes do not repeat the ADC self-rejecting admission check.
- NG Transfer, Shuttle and Conveyor: state reads physical feedback; movement and
  ejection phases retain in-progress intent across Stop. Keep that intent separate
  from material presence. The existing tests cover interruption between sensors
  and cancellation during RUN setup; no additional exception layer was justified.
- Start/Home enter through UI commands and dispatch work with Task.Run; Reset also
  enters directly from the physical DI event. Reset rechecks safety after awaited
  hardware initialization, so that check is not duplicate admission. The IO reset
  runs off its monitor thread to avoid waiting for itself. Keep hardware-specific
  alarm handling; do not assume these paths all run on the UI dispatcher.
- Lighting reads the current channel/level at capture or live start. Navigation
  stops live view before Settings can edit the channel; active capture excludes
  Settings. Model-path changes reload on CheckReady/Segment; training explicitly
  reloads after replacing the same file. Existing operation/session ownership
  excludes concurrent settings edits. No new cache, file watcher or lock needed.

## Remaining evidence needed

The teaching follow-up reproduced a narrower HoldButton problem: failed mouse
capture still started Virtual jog, and start eligibility disabled the button
while that motion ran. Capture failure now prevents starting. Once capture succeeds,
the button temporarily detaches Button.Command so admission of another operation
does not disable the active hold; release restores it. PressCommand.CanExecute is
still checked before starting; release/capture loss and the existing common
cancellation path still stop the owned motion. This desktop test process cannot
acquire real mouse capture, so actual held-mouse travel/release remains a manual
acceptance item. The capture-failure case was verified before and after the fix.

The Station 2 Z-order question was subsequently resolved: both heads fasten at the
shared Safe Z using cylinder strokes only. The common Z axis moves for IPM bolt
pickup, returns to Safe Z before raising Head 1, and stays there during fastening.
The shooting
recovery question was subsequently answered: tube detection ON/head vacuum OFF
requires operator clearing, using manual shooting air rather than automatic
refeeding. See the Station 2 shooting follow-up below.
Native AlphaMotion IO and HIK exposure/gain effects still need equipment checks;
the managed SDK probes only verified which commands and settings are sent.

## Completion and test limit

- Close a branch after its real callers, yielding/reentry points and cleanup have
  one explained owner, and the affected existing check passes when code changed.
- Reopen only for a changed caller, a changed requirement, or new failure evidence.
- Defer unresolved physical behavior explicitly; do not encode an assumed answer.
- A read-only or documentation pass needs no equipment test. A small code edit
  gets a build and its affected path; shared motion/cancellation changes may need
  wider verification. Do not rerun whole-equipment flows for every UI edit.

The proposed UI exception edits above are complete. This document is the review
method and decision record; no runtime Guard/Context framework is needed.

## Teaching follow-up

- Supply and Station teaching share `TeachingMotionView` for feedback coordinates,
  Jog/Step selection, movement buttons and block reasons. Axis direction is one
  enum command parameter, not six separately implemented commands.
- Step distance is 0.01 / 0.1 / 1 mm. A step reads actual feedback and calls its
  existing handler/gantry; it uses configured motion speeds, not the Jog-speed
  selector. The configured target range controls step admission. No position
  clamp, extra taught flag, motion interface or recovery snapshot was added.
- New steps share existing mechanical checks, operation cancellation and shutdown
  awaiting. Supply keeps separate axis moves and its buffer Y/Z restriction;
  Inspection has no Z and requires the NG pickup raised before XY movement.
- `MachineState.ManualBlock` is the single definition for machine-level manual
  admission and its displayed reason, preserving the existing allowed conditions.
- Taught/current coordinates use three decimal places. Teach/save behavior is
  explicit: machine points auto-save, ordinary recipe points require Save Recipe,
  image clicks auto-save, and buffer points are staged until Apply & Save Buffer.
  These existing persistence semantics were not silently changed.
- The inspection bolt editor uses two columns so Remove is not clipped at the
  actual side-panel width. Both screens were rendered at 1872 x 850 client size
  and inspected; shared coordinate foreground and wrapping were corrected.

Verification: Release solution build, 35 existing MachineLifecycle tests, and the
existing TrainingUiVerification runner's focused `--teaching-only` path passed.
That path checks Virtual X/Y steps at all three distances, Supply Z, Stop, range
limits, buffer/NG restrictions, persistence hints, and both actual WPF views with
no binding errors. It does not run model training or the whole equipment suite.
No physical motor, IO board or camera was operated.

### Recursive teaching pass 1 — settings-save lifetime

The user requested another recursive review of teaching. The existing `ibtm`
heartbeat was resumed with a teaching-only scope and the same evidence-based stop
condition; no additional automation was created.

- Followed new Step dispatch through Supply, Placement, Fastening and Inspection
  owners, then page deactivation/shutdown. The new command is included in cancel
  and await paths; no additional motion layer or guard was justified.
- Found and reproduced an actual async gap in buffer settings saving: the task
  was pending while `MachineState.ManualControlsEnabled` was still true. This is
  an await boundary, not hypothetical concurrent UI-thread execution.
- Supply machine/buffer saves and Station machine/image-reference saves now use
  the existing operation scope while writing files. This blocks new manual work,
  recipe edits and navigation to editable pages until the write finishes. No
  additional lock, busy flag or snapshot was introduced. File saving itself still
  finishes if Stop is pressed; motion cancellation was not delayed or changed.
- Rechecked the related Station coordinate and image-pin paths using the real
  commands. All three saves actually yielded; manual actions were disabled while
  pending and restored after completion. Release solution build passed. The
  existing runner's `--teaching-save-only` path was used; no full-suite rerun.

Next passes: selection/group changes and leave/reenter behavior across all teaching
units; recipe/image load and save ownership; remaining visible disabled-state
explanations. Do not repeat the mouse-capture probe without a new usable desktop
context. Pause the heartbeat after these branches and changed-call-path rechecks
have no remaining actionable findings.

### Recursive teaching pass 2 — page selection and lifecycle

- Reproduced a supported UI action: pressing the already-selected Supply Teaching
  navigation radio button rebuilt its point list, losing unapplied buffer teaching
  and the selected point even though the displayed page did not change.
- Navigation now only assigns SelectedPage. The existing deactivation/activation
  work lives in the generated property's changing/changed callbacks, so it runs
  only for a real page transition. No extra page guard, draft cache or save prompt
  was added. The buffer hint now explicitly says to apply/save before leaving;
  genuine exit/reentry retains the existing discard-unapplied-changes policy.
- The existing focused Supply fixture failed on same-page selection before the
  change and passed afterward with the same taught point and unapplied value.
  The actual application's `--ui-only` runner also passed page navigation,
  Placement/Fastening/Inspection group selection, capture/save and image Move To.
  This was a UI pass, not another full automatic equipment-flow run.
- Followed point/group changes through cancellation and shutdown. Supply point
  changes and Station motion-group changes cancel the current owner token; an
  ordinary Station point selection does not redirect an already captured Move To
  target. Both views include Step in cancel and awaited shutdown. No new handling
  was justified by these read-only paths.
- Reviewed save/load callbacks: global operation ownership blocks edits while a
  recipe is replaced, and the existing Changed event rebuilds the affected point
  lists. Save completion after leaving the page does not use the new selection to
  choose a storage owner; it retains the original TeachingPoint target.

Next candidate to verify: fastening locating-pin teaching invalidates the paired
lower-right setting, but its previously built list row may still display old
numeric coordinates. Check that against the disabled Move To/UI behavior before
changing the representation; do not add a separate IsTaught cache. Then finish
the remaining teaching save/load and UI explanation audit and close the heartbeat.

### Recursive teaching pass 3 — independent references and first bolt Z

Historical note: per-bolt Z teaching and its special XY-only command were later
removed when both heads were confirmed to fasten at the shared Safe Z.

- The user confirmed that re-teaching an upper-left locating pin must preserve
  the existing lower-right pin. Removed automatic lower-right clearing from the
  carrier reference and both fastening-head references. The existing coordinate
  refresh remains; no new taught-state flag or nullable display layer was needed.
- Reproduced a separate first-depth teaching bug: a bolt with taught XY and
  references but no Z was built with placeholder display XY. Teaching its first Z
  enabled Move To without refreshing that XY, and the command consumed the stale
  placeholder. BoltPointZ application now calls the existing bolt-coordinate
  updater after assigning Z. Re-teaching Z still ignores the current machine XY.
- Focused coordinate checks passed for both heads: missing-Z admission, first and
  repeated Z, independent upper-left re-teaching, and an initially untaught
  lower-right remaining untaught. Carrier pin/marker checks passed as well.
- Rechecked the affected command path: Teach applies before enabling Move To;
  changing motion groups rebuilds coordinates from current recipe/settings;
  image-origin teaching refreshes markers and saves its owning setting. Existing
  manual/motion hints and disabled commands still derive from actual readiness.
  No additional state, lock, exception handler or full-suite rerun was added.
- The existing teaching-save-only runner passed pending-save admission, Supply
  Stop/retry, buffer staging and same-page navigation. Release solution build
  passed with zero warnings/errors. No physical equipment was operated.

This teaching review cycle is closed and the existing `ibtm` heartbeat is PAUSED.
There are no further evidenced code changes in the reviewed teaching scope. This
does not certify physical operation: captured hold-to-jog on a usable desktop,
real driver feedback, and the previously listed mechanical head-change/shooting
questions remain for equipment confirmation. Do not restart unchanged probes or
expand into unrelated automatic work without a new request or new evidence.

### Home prerequisites — explicit user rule, 2026-09-07

The user requires an empty machine and already-raised handler cylinders before
Home is permitted. NG conveyor carriers count; NG shuttle height does not. Only
the units actually being homed need their cylinder-up feedback. This supersedes
Inspection's former automatic gripper-open/pickup-up preparation.

- MachineController shares one live-DI HomeBlock check between Home All and manual
  individual-axis Home. Carrier inputs are always checked; cylinder states use
  their owning handlers' existing Up/Down interpretation. No presence cache,
  clearance coordinate, additional recovery state or exception hierarchy was added.
- Placement Handler, bolt Head 1/Head 2, and NG Pickup must already be Up.
  A missing Up or conflicting Down feedback blocks the relevant Home. Existing
  supply rotation/positive-limit initialization and Z-before-XY order are preserved.
- Relevant DI changes refresh the command state. Loss of a prerequisite during
  Home cancels via the existing operation lifetime; no automatic cylinder recovery.
- OUTPUTS previously depended on homed motion, which would prevent the operator
  raising cylinders beforehand. Its availability now depends on ready I/O, idle
  Manual mode, safety and no alarm. Axis-teaching admission is unchanged. The
  output window closes when that admission is lost, including when Home starts.
- Home All's disabled tooltip and operation/manual display expose the reason.
  Existing lifecycle tests passed (36 at that run); two new condition-loss cases
  passed separately. The actual WPF --ui-only runner passed navigation, teaching,
  capture/save and IO windows without unexpected exceptions. Physical hardware
  was not operated. No review automation was restarted for this explicit change.

### One-button cylinder preparation

The user requested one button instead of operating every cylinder separately.
Added RAISE CYLINDERS beside the Home/Start controls. This is separate from Home:
it commands only the required enabled units' lifts and waits for their existing
mapped DI/timeout. Owner methods remain in Placement, Fastening and NG Transfer;
the controller owns common admission, cancellation and unit-alarm attribution.
It does not command axes, grippers, vacuum, stoppers, plates or NG shuttle height.
Carrier detection, loss of Manual/safety readiness, Stop or shutdown cancels the
waits without restoring pneumatic outputs. Home still requires confirmed Up DI.

Focused Virtual checks passed for the required head cylinders, no axis/other-actuator
movement, carrier admission, repeated Up, disabled-unit exclusion, Stop and
timeout. The actual WPF --ui-only run passed with empty binding/exception logs.

### Cylinder clearance for every X/Y move — 2026-09-07

The user explicitly extended the raised-cylinder rule to automatic operation as
well as teaching, Jog and Home, and clarified that it applies only to cylinders
raising/lowering the heads. IPM fixing/lift cylinders are excluded. The earlier
shooting-head-down travel between bolts is superseded.

Placement enters buffer X/Y with its Handler up and IPM down for pickup. Fastening
raises Head 1/Head 2 for every X/Y move and lowers the working head at the
target. Existing PCB, IPM seating and IPM final passes are preserved.

Motion command activity distinguishes horizontal movement from Z-only movement.
The interlock therefore permits Z travel and checks again before X/Y begins.
AJIN Home and Jog publish the start check before issuing the native motion command.
Both Up and Down inputs are read through the owning units' existing cylinder states;
there is no saved cylinder-position flag. Teaching buttons use the same conditions.

Verification before the IPM scope clarification: all 113 Virtual tests passed, including lowered/conflicting cylinder
inputs, Z-only travel, feedback loss during X/Y and between Travel Z and X/Y, and
the full carrier flow. Release build had no warnings/errors. The actual WPF
`--ui-only` pass completed navigation, Home, teaching, carrier capture and IO windows
without unexpected exceptions. No physical hardware was operated.

After the IPM clarification, the 52 Placement/lifecycle tests passed. They verify
Home with IPM down, IPM feedback changing during uninterrupted X/Y travel, the
one-button raise leaving IPM untouched, and buffer entry with IPM down. Release
solution build passed with no warnings/errors.

The fastening table cylinder was subsequently removed from the machine definition.
Only Head 1 and Head 2 participate in fastening lift control and clearance checks.
Its IO declarations, mappings, UI section and saved local mapping entries were
removed without renumbering the remaining hardware channels. All 112 Virtual
tests passed; the Release build and actual WPF `--io-only` pass also succeeded.

### Bolt teaching adjustments — 2026-09-07

The user needs to align bolt centers with the head lowered. Fastening Jog/Step
therefore use a distinct single-axis adjustment command, keeping the current Z
and selected teaching speed. Saved-position moves, Home and automatic operations
retain the head-up rule. Placement, Supply and NG teaching restrictions are not
relaxed. MotionCommand records only the active command kind, not cylinder state.

Hold-to-jog uses a cancellable move toward the configured axis bound; releasing,
Stop, Auto selection or loss of motion readiness stops it through the existing
cancellation path. The speed selector is available for fastening Step as well as
Jog. No ignore-interlock argument, global bypass setting or new wrapper class
was added. Mechanical clearance and suitable physical adjustment speeds still
need commissioning; this change does not establish that the entire axis range
is collision-free with a lowered head.

Verification: all 115 Virtual tests passed, followed by focused checks after
command-kind cleanup and motion-fault handling. They cover head-down X/Y steps,
unchanged Z, hold release, software limits, Auto/Servo-Off cancellation, saved
position/Home restrictions and hardware-motion errors reaching the existing alarm
without an unhandled UI exception. The actual WPF `--ui-only` pass also succeeded.

### Teaching position ownership — 2026-09-07

Each unit now defines its teaching positions beside its settings/recipe through
`GetTeachingPositions`: target, axes, read/apply functions, availability and the
owning settings object. `TeachingPosition` is WPF-independent; `TeachingPoint`
keeps only the editable/display coordinates. The UI no longer switches targets
to apply coordinates, choose a settings file or convert bolt coordinates.
`SupplyTeachingPoints` and `StationTeachingPoints` were removed.

Buffer edits remain staged until Apply & Save Buffer, and unrelated teaching
does not overwrite them. Handoff coordinates are updated in place because
BufferStage shares those objects. Re-teaching one locating pin preserves the
other; dependent bolt rows reread the owner's coordinate conversion. Recipe
replacement rebuilds the definitions against the current recipe.

Verification: 120 Virtual tests passed, including two focused teaching tests.
Release build: no warnings/errors. Actual WPF `--ui-only`: passed navigation,
carrier capture/save and image-taught Move To, with empty binding/exception logs.
Manual Home still delegates cylinder requirements to the unit and motion safety
to the motion layer; UI enablement and in-flight cancellation checks were retained.
No physical hardware was operated.

### Two-pin carrier teaching — 2026-09-07

Inspection now teaches only the backup plate's upper-left and lower-right pins.
Align each pin with the live camera center and Teach the actual XY; the same
`CarrierReferenceSettings` coordinates define both the carrier scan endpoints
and the fastening coordinate reference. Separate scan-position settings and
teaching targets were removed. Re-teaching one pin retains the other.

Scan Carrier becomes available after both pins are defined, stops Live before
capture and selects a bolt afterward. Image clicks teach bolts only; pin markers
remain visible on the overview. Scan coverage uses the existing FOV/overlap and
serpentine path, without another coordinate copy or hard-coded scan margin.

Verification covers positive/reversed scan directions, actual-XY pin teaching,
first/second-pin scan enablement, saved image capture and subsequent bolt
teaching. Live window operation and physical hardware were not exercised in
this pass.
Release compilation succeeded and all 123 Virtual tests passed.

### Teaching workflow follow-up — 2026-09-07

The user resumed recursive teaching review. Bolt teaching separates known XY
from an unknown Z: Move XY at Safe Z reaches the calculated location through the
owning fastening gantry, with both heads raised. Full Move To remains disabled
until Z is taught. Automatic GetBoltPosition still requires Z; no default height
was introduced. The new command is included in cancellation, shutdown and recipe
editing exclusion; selecting another Station teaching point stops pending motion.

Supply/Buffer teaching now also includes Placement Safe Z from its existing
settings definition. Both teaching pages share Teach/Move To and their selected
point/save hint beside the Jog controls. Parent-page duplicate controls were
removed. Related tests and first-Z movement tests passed (18 affected tests);
Release compilation passed. No actual window or physical-hardware test was run.

Supply buffer Z fine-adjustment remains restricted pending the user's mechanical
answer. Manual buffer exit, teaching IO actions, read-only access before Home,
image workspace and next-point workflow remain in the resumed review queue;
see artifacts/continuous-review.md for the current handoff.

### Owner-controlled teaching IO — 2026-09-07

Supply, Placement, the fastening gantry and NG Transfer now declare their teaching
outputs through `GetTeachingOutputs`. Each declaration binds the existing unit
method, not a raw UI `SetOutput`. Supply has Nest/IPM/rotation; Placement has
handler/IPM lift, gripper, vacuum and rotation; fastening has both head lifts and
vacuums; NG Transfer has pickup lift and gripper. Subsequent changes added the
station backup-plate controls and momentary shooting air. Conveyor drive, shuttle,
feeder and automatic escape sequencing remain read-only in these teaching panels.

The shared related-IO template shows ON/OFF beside supported outputs and retains
both actual feedback inputs. It reuses IoSignals objects; no duplicate cylinder
state or output cache was added. The Supply-only actuator enum, switch, feedback
properties and three duplicated buttons were removed. Output is not treated as
evidence of movement completion, and repeated ON/OFF is allowed.

Commands use existing manual-operation exclusion, cancellation and owner timeout
alarms. Stop, point/group changes, page exit and shutdown cancel pending commands
without reversing pneumatic outputs. Recipe switching is excluded during output
commands. Supply rotation still raises to its taught Safe Z and is forbidden
inside the buffer; Placement rotation requires its Safe Z and raised handler.
Head-up requirements for normal XY and the existing fastening-only single-axis
fine-adjustment rule are unchanged. IPM up/down is not an XY interlock. Supply
buffer Z restrictions have not been relaxed.

Verification: Release compilation and five affected Virtual tests passed,
including DI conflicts, duplicate ON, delayed feedback, cancellation/retained DO,
owner timeouts, rotation and head-up rules. The verification runner now has a
`--teaching-bindings-only` branch that constructs WPF views off-screen without App
startup, machine initialization, Home or IO commands. All five teaching contexts
bound their controls with zero binding warnings and remained disabled before Home.
No real equipment or live application window was operated in this pass.

### Read-only teaching before Home — 2026-09-07

Teaching navigation now needs idle Manual mode and an enabled relevant unit, not
Homed/Servo readiness. Selecting points, viewing saved coordinates/images, related
IO and loading another recipe remain available before Home. The page no longer
redirects to Operation merely because readiness is missing or an alarm is active.
Auto selection/running still leaves teaching; existing operation cancellation is
unchanged. Loss of readiness stops live camera display without hiding the page.

Motion, output, Teach and buffer-save commands retain their existing conditions.
One `CanEditTeaching` getter uses MachineState.ManualControlsEnabled for bolt
Add/Remove and the scale/overlap/preset editors. These previously relied on the
whole page being disabled. Recipe New/rename/Save also require manual-control
readiness; recipe selection remains possible while idle. The page is still gated
during recipe Save/Load to prevent edits against a replacing recipe.

Verification: five affected Virtual tests passed, including edit readiness before
Home, after Home, during another operation, in Auto and after Servo Off. Off-screen
WPF checks confirmed pre-Home navigation survives state refresh, selects points,
and leaves both IO commands and editable fields disabled with no binding warnings.
Release compilation and diff whitespace checks passed. No real hardware or actual
application Home/axis/IO commands were executed.

### Image workspace and consecutive teaching — 2026-09-07

Inspection teaching now gives the image 960px of the 1724px content width used by
the 1920px shell. Point selection and related IO share a resizable right column;
camera controls occupy their own wrapping bottom row instead of covering the
image. Other motion groups use the central point list and full-height IO panel.
The left motion panels scroll when necessary rather than clipping controls.

Both teaching pages use the same Previous/Next commands and TeachingPointList.
Selection alone never saves coordinates or moves an axis; existing selection
cancellation stops a pending teaching command. Navigation stops at list ends and
scrolls the selected row into view. Supply navigation follows its displayed motion
groups. Fastening teaching groups each head's pins and calculated bolt XY together,
without changing recipe bolt order or automatic execution. Inspection lists its
two pins, image bolt points, then NG positions.

Untaught pin/image/Z positions display an em dash, not a plausible zero. Taught
image points show recipe-relative XY. Availability changes also refresh the label
when numeric coordinates happen to remain unchanged. No new teaching-state flag
or cached point index was introduced.

Removed the inspection page-wide capture/point-command disable so STOP remains
reachable. Existing command and editor permissions still prevent concurrent edits
or movements; deliberate point/group changes cancel the existing operation.

Verification after the final ordering change: five affected Virtual tests and the
off-screen WPF binding/layout check passed with no binding warnings. The layout
check covers read-only permissions, STOP, next/previous boundaries and scrolling.
Its hidden HwndSource supplies WPF layout without showing/activating an app or
initializing hardware. An initial scrolling failure belonged to the source-less
test host; no production delay or dispatcher workaround was retained. Inspected
artifacts/teaching-bindings-bin/teaching-layout.png; the isolated fixture contains
no camera image. Release compilation and diff check passed. No full-suite or
physical-hardware validation is claimed for this cycle.

Independent teaching-review work and changed-path checks are complete. Supply
buffer Z fine adjustment and manual exit remain pending the user's mechanical
answer; existing restrictions and automatic exit were not changed.

### Full-review implementation — 2026-09-07

All seven approved findings were implemented, then followed through callers,
reverse/error paths, cancellation, persistence and WPF binding/layout:

- MachineController is the single source for StartBlock/CanStart/Home permissions.
  The operation view retains only display mapping and its existing coalesced updates.
- Alarms retain original exception details (including native codes/stack) and expose
  them through the shell alarm tooltip. IO monitor faults carry their exception.
  Teaching capture handles camera, motion and file errors at its command boundary;
  motion faults also set the machine alarm. Cancellation is not reported as a fault.
- Carrier PNG decoding and rename-copy run off the UI thread. Only the current read
  publishes; reads are cancelled and drained on replacement/live/capture/navigation/
  shutdown. Original full-resolution images and mutable operating recipe are retained;
  no cropped-image replacement or recipe snapshot was introduced.
- Inspection-only display requires its reference pins, not NG positions. When valid
  NG teaching exists it still aligns the shared picker at carrier and shuttle. A
  single taught fastening head also suffices for display. Zero-length references
  remain undefined and never generate NaN transforms for hidden WPF elements.
- Shared MachinePlan anchors now drive station plates/carriers/stoppers, supply,
  buffer, placement and bolt heads. Carrier border/padding and bolt marker centers
  come from the same layout values; an actual marker-center offset was corrected.
- Ajin completion now permits feedback settling up to the existing common timeout.
  InMotion OFF alone is not success; InPosition and fault/cancellation checks remain.

Final Release build passed with zero warnings/errors, and the full Virtual suite
passed 131/131 after the last refinements. Diff check is clean. Off-screen WPF
verified teaching permissions, navigation, operation bindings and actual carrier
content/bolt center alignment with no binding warnings. The existing recursive
review heartbeat was paused on completion. No real app Home/axis/IO commands or
actual device settling/optical accuracy verification were performed.

### Station 3 teaching-to-training workflow — 2026-09-07

Followed reference-pin teaching, full-resolution carrier scanning, image-point
teaching, recipe save/reload, labeling, CPU training and saved-model review.
The existing pin/scan test now verifies the saved relative bolt coordinates,
mm/pixel and nine image tiles, then reloads and returns the Virtual camera to
the same taught XY.

Rendering an actual populated training review exposed a WPF exception: Run.Text
defaulted to TwoWay for the read-only Review.ImageCount. Display runs now bind
OneWay. The training toolbar separates source acquisition from mask editing;
the empty image area and dataset/model-save guidance describe the next action.
Review Validation Set reruns validation without retraining, changing the operating
threshold or moving hardware. Cancellation and shutdown drain that command too.

An isolated hidden-WPF run labeled five synthetic samples, trained one CPU epoch,
reloaded identical validation results from disk, checked that model bytes and
threshold were unchanged, and exercised cancel and a missing dataset. The
populated 1724x852 training view had no binding errors or off-screen buttons.
This verifies software flow, not model accuracy. The focused inspection/recipe
suite passed 11/11. No original carrier frames were replaced by cropped storage,
and no real hardware or actual-app motion/IO commands were executed.

### Central inspection ROI — 2026-09-07

Separated the centered source ROI from the fixed model input size. The inspection
settings expose `Central ROI Size (px)`; `BoltImageInput` extracts only that square
and bilinearly resizes it to 128x128. Label preparation and automatic inspection
use the same function. Segmenters, saved training pairs and validation operate on
normalized inputs, with no second crop. Original camera frames remain unchanged.
The initial ROI is still 128px until adjusted to the real optics; changing the ROI
continues to use the existing model and leaves saved samples unchanged. Retraining
is not required merely because the ROI changed; mask quality and area thresholds
are evaluated separately.

Focused inspection/recipe tests passed 13/13, including central-only sampling,
upscaling/downscaling and padded source strides. The isolated CPU/WPF workflow
also passed with a 144px ROI resized to 128px, verifying pixel-identical label and
inspection preprocessing, model save/reload and cancellation. No hardware was run.

### Training owns model weights, not operating thresholds — 2026-09-07

Removed automatic area-threshold calibration and its scoring helper. Training
still selects the weights with the lowest pixel-level validation loss, but
publishing a model no longer edits or saves operating inspection thresholds.
Corrected the ROI tooltip and documentation: resizing a changed ROI can use the
existing model, and retraining is not a mandatory consequence of that setting.

The isolated CPU/WPF workflow verified that both mask and area thresholds survive
successful training unchanged. Cancelling after an actual completed epoch kept
the previously published model bytes and thresholds and removed temporary weights.
Image/mask alignment, saved-model review and bindings also passed. This remains
a synthetic software-path check, not evidence of real bolt-detection accuracy.

### Database-only bolt training and polygon labels — 2026-09-07

This replaces the earlier file-pair dataset and checkpoint workflow above.
The training project now owns SQLite storage for full-resolution source-image
BLOBs, each source ROI, one polygon per sample, explicit Unlabeled/Empty/Bolt
labels, included/excluded status, stable training/validation membership and the
active model weights with training metadata. No training image/mask/model files
or folder/import/export UI are used. Captures are saved one by one before labeling;
cancelled capture batches retain completed database inserts.

The page lists metadata and loads only the selected original for its Original
and ROI/Polygon tabs. Saved polygons reopen for editing. First-vertex click or
Enter closes the polygon; right-click/Backspace undoes a point; Escape clears
the current unsaved polygon without ending the session or modifying DB data.
Masks are generated, not separately persisted. Training reads samples by batch.
TorchSharp saves/loads weights through memory streams. Publication replaces a
single DB model row; cancellation before publication preserves the prior model.

The isolated hidden-WPF workflow passed source-pixel roundtrip, identical ROI
preprocessing, concave polygon rasterization/persistence/reopen, next-sample
isolation, cancel-without-overwrite, exclusion/split editing, actual CPU training,
fresh operational segmenter loading from DB, model review and epoch cancellation.
It also confirmed that the dataset directory contains only SQLite, without
standalone training images/masks/weights/checkpoints. No hardware was initialized.
The focused inspection and missing-model startup tests passed 7/7. Existing
off-screen teaching/navigation/IO bindings also passed without machine commands.
Synthetic model results verify software flow, not real inspection accuracy.

### Training defaults, advanced settings and early stopping — 2026-09-07

The training project owns observable DB-backed settings: Max Epochs 50, Batch
Size 8, Learning Rate 0.001 and Early Stop Patience 10. Train saves these settings;
reopening restores them. Batch size and Adam's learning rate now use the selected
values. Advanced exposes these fields and patience without duplicating settings
in the view model. Nonpositive counts or nonfinite/nonpositive learning rates
disable Train.

The trainer records the best epoch and stops when that epoch has not improved for
Patience consecutive epochs. Equality does not reset patience. The normal review
and DB publication use the best weights, and the model row stores actual completed
epochs. Early stop is separate from cancellation; cancellation still preserves the
previous model. The UI shows BEST EPOCH and Completed / Completed · Early Stop.
The right panel scrolls when Advanced or review content requires more height.

An isolated CPU/WPF check exercised nondefault batch size and learning rate,
settings roundtrip, invalid setting availability, a one-epoch limit, and a frozen
parameter run (test-only tiny learning rate) with patience 2: it completed at epoch
3, selecting epoch 1. Epoch cancellation still kept existing model bytes. This is
control-flow verification on synthetic images, not production accuracy validation.

### Product-owned inspection conditions — 2026-09-07

Moved central ROI size, mask probability threshold and minimum mask ratio from
machine settings into `BoltInspectionRecipe`. The existing product recipe Save,
New and Load operations own these values. Settings now holds only camera hardware
parameters; Station Teaching / Inspection Gantry exposes the three recipe fields.
Editing numerical inspection conditions is allowed in idle Manual before Home,
without relaxing motion or output controls.

The detector and model review resolve the active recipe's conditions instead of
holding an obsolete nested object after recipe replacement. Training retains its
own DB hyperparameters and each sample's ROI and polygon. Revisiting model review
applies current recipe thresholds to its predictions without retraining.

Focused recipe, inspection, training DB and missing-model startup tests passed
15/15. Hidden WPF checks verified editable field bindings, New-recipe replacement,
and the same DI detector reading updated ROI and both thresholds. An isolated CPU
training run verified review updates after recipe replacement while model bytes
remain unchanged. No hardware was initialized or commanded.

### Machine DB and ownership follow-up — 2026-09-07

Reviewed ownership, then persistence call paths, then UI/restore/capture behavior.
Machine settings, product recipes and carrier PNGs now use `IBTM.Storage` and
`Data/Machine.db`; the training DB remains independent. Section DTOs stay with
their units and no longer save themselves. Buffer teaching saves one settings
batch. Recipe metadata/image replacement and Save As are transactional, with
live name/image-list changes only after successful storage.

Exposure, gain, light level and carrier scan overlap moved into the inspection
recipe; camera identity/FOV, light channel and fixed mechanical teaching stayed
machine-owned. Hidden WPF checks traced active and replacement recipe values into
single capture, preview and segmentation, and loaded all Settings tabs without
binding warnings. Database backup/restore uses SQLite's backup API. Restore queues
normal window close after its own command completes and applies on next startup,
preserving the previous DB. AJIN .mot and training data are explicitly excluded.

Tests covered settings-batch rollback, failed recapture/Save As, legacy migration
rollback on a missing image, preservation of migrated optical parameters, import
idempotence, backup/restore and previous-DB recovery. The legacy importer streams
images one at a time; original JSON/PNG files are untouched. Full Virtual regression
passed 139 tests. CPU training/labeling/ROI regression still passed independently.
No physical controller, camera, IO output or axis was initialized during these checks.

### Recursive storage / training follow-up — 2026-09-07

Revisited storage internals, then their callers, then screen lifecycle and binding
behavior. Carrier recapture now encodes/inserts one lossless PNG at a time and
clears EF tracking between inserts. Save As copies BLOBs within SQLite. Neither
path crops originals or reduces resolution. A single transaction still covers
metadata and all images; partial writes and cancellation retain the prior recipe.
The capture cancellation token now reaches the image-save operation.

Loading a recipe refreshes dependent views before saving its last-selection
preference. A preference-save failure can no longer leave the active recipe and
display referring to different objects. Removed the redundant Home requirement
from New/Save/name editing; the toolbar still requires idle Manual, and physical
teaching/output interlocks remain unchanged.

The training session distinguishes an active label editor from a hidden sample.
Leaving the page cancels its command and retains Busy until that command ends;
after completion, a hidden sample no longer blocks machine work. Returning to
the sample restores the session. Full originals remain in the training DB; both
training and inspection feed only the resized ROI to Tiny U-Net.

Existing tests were extended rather than adding another test suite: image-save
cancellation and transaction rollback, selection-save failure with UI notification,
and off-screen WPF checks for pre-Home recipe buttons and training navigation.
The isolated training check still exercises actual CPU training, polygon/ROI
edits, cancellation, saved-model reload and early stopping on synthetic data.

Final verification: 139/139 Virtual tests, both off-screen WPF checks and CPU
training passed. Release solution and Virtual application builds completed with
zero warnings/errors after the test process exited. No production DB was migrated,
restored or edited, and no physical hardware was initialized.

### Optional inspection image collection — 2026-09-07

Added the Off / All / NgOnly enum and selector in the Bolt Training header. Apply
saves it without starting training; the header also shows automatic collection
count and allocated DB size. The completed bolt inspection supplies its original
frame through an event connected by host DI to the training-owned collector.
PNG encoding and SQLite writes execute on the inspection worker one image at a
time, without a second capture or an accumulating frame queue.

Collected samples are Unlabeled. Their immutable inspection evidence records
recipe, bolt/heat-sink identity, time, original ROI and judgment separately from
editable annotation ROI/polygon. Existing samples survive an additive migration.
Forced insert failure verified the requested policy: preserve the judgment,
continue inspection, pause only collection, and show the underlying error in the
shared shell banner. Apply acknowledges the error after storage is repaired.

Hidden WPF checks caught the first placement pushing validation navigation below
the initial viewport; moving collection controls into the header restored access.
Mode persistence, DI capture/result wiring, failure isolation, full-image PNG
roundtrip and existing CPU training/annotation workflows passed. Full Virtual
regression passed 140 tests; no real SDK or production database was operated.

### Selected-image reinspection — 2026-09-07

`Inspect Selected Image` runs the saved Tiny U-Net on the displayed central ROI,
using the same crop/resize and threshold calculation as validation review. It
bypasses training-dataset eligibility: unlabeled/excluded samples can be inspected
even when no usable training or validation split exists. Inference and image
conversion stay on the worker; no camera/motion/IO command or DB write is involved.

Both review modes reuse `BoltModelReview` and one result panel. Current OK/NG,
mask ratio and overlay are separate from recorded judgment/ROI. Explicit labels
alone enable Match/Mismatch; recorded NG never becomes a ground-truth Empty label.
ROI changes, sample changes and Close Sample clear selected-image results.
Cancellation includes this command and suppresses late publication.

Extended the existing isolated CPU/WPF workflow rather than introducing another
test suite. It checked missing models, an excluded unlabeled image with all training
samples disabled, exact mask/ratio agreement with independent inference, unchanged
weights/labels/ROI/recipe, historical NG versus current OK, result invalidation,
command cancellation and both review layouts. Synthetic images verify the workflow,
not real bolt-detection accuracy. No physical device or production DB was used.
Final checks: 140/140 Virtual tests, CPU training/reinspection and both off-screen
WPF checks passed. Release solution and Virtual app builds had zero warnings/errors.

### Prepared training inputs and one ROI editor — 2026-09-07

Training now prepares each included, labeled sample once: read its original PNG,
apply the saved central ROI and 128×128 resize, then rasterize its polygon. Epochs
reuse BGR bytes and byte masks (64 KiB per sample) rather than repeating those
operations. Full originals and all-sample float tensors are not retained. Active
batch tensors keep their normal Torch disposal; session arrays need no Dispose
wrapper or forced GC and are not written to disk. Preparation remains cancellable
and does not replace the existing model. The screen distinguishes preparation
from the epoch loop.

Removed the separate ROI preview control. One editor now displays the full image,
central ROI outline/corner handles, and polygon in original-image alignment.
Wheel zoom, middle-drag pan and Fit Image/Fit ROI change only the view transform.
ROI resizing preserves polygon locations, including odd-sized crops. The saved
128×128 polygon convention and DB format are unchanged. Selected-image predictions
overlay that same ROI and can be toggled independently of the label; validation
review keeps its separate image because it may refer to another sample.

The existing hidden WPF/CPU workflow checks ROI drag binding, zoom anchoring, hit
coordinates, polygon restoration after resize, predicted-overlay toggling, exact
prepared bytes/masks, preparation cancellation and model persistence. In an
isolated test DB it temporarily renames Samples after preparation: training,
validation and final review still complete without reading original images again.
The test restores the table afterward. Synthetic images verify implementation,
not real bolt accuracy or a production training-time estimate.
Final verification passed 140 Virtual tests, both off-screen WPF checks, actual
CPU training and reinspection. Release solution and Virtual builds completed
without warnings/errors. The final render also retains the shared checkbox theme.

### Live judgment parameter refresh — 2026-09-07

The result panel now edits Mask Threshold and Min. Area (%) against the active
inspection recipe. Setters recalculate the current overlay/ratio/judgment directly;
area-only changes retain the overlay. There is no second recipe/settings copy,
model reload, training run, DB write or timer. Normal recipe Save remains the
persistence boundary. Percent display is converted to the existing ratio field.
Numeric range/nonfinite validation stays at these two UI inputs.

To keep validation-set tuning light, each review sample sorts its predicted
probabilities once on the inference worker. Subsequent area queries use an exact
lower-bound search, including duplicate values at the threshold. Only the visible
mask is redrawn when its probability threshold changes. Original spatial masks,
recorded inspection results and annotation polygons are unchanged.

The existing isolated CPU/WPF workflow exercised textbox and slider bindings,
immediate result/overlay changes, area-only overlay reuse, percent conversion,
invalid input correction, recipe replacement and single-image overlays. Temporarily
renaming the model table proved parameter changes do not read weights. Inclusive
threshold/duplicate-value checks matched direct counting. ROI invalidation and
training cancellation continued to pass. Production data/hardware were not used.
Final verification passed all 140 Virtual tests and both isolated WPF workflows,
including actual CPU training/reinspection. Release solution and Virtual app builds
completed with zero warnings/errors. The final render confirms readable numeric
formatting and that the result controls fit without adding another image panel.

### Recipe saving from Bolt Training — 2026-09-07

Bolt Training now uses the application's existing recipe toolbar: a read-only
save-target name and Save Recipe. New/Load stay on the teaching pages. The same
RecipeEditor.SaveCommand writes Machine.db; the training project has no new host
or storage dependency and no additional save implementation. Tooltips distinguish
recipe persistence from image collection, labels and model weights.

An idle open training sample still blocks equipment operation, but does not block
recipe saving. EquipmentBusy is the existing non-training portion of IsRunning;
the latter retains all of its previous conditions. Training work disables Save,
and Save/Load disable the training page until the existing command completes.
No new busy flag, lock, snapshot or exception handler was introduced.

The isolated WPF harness verifies the shared command, read-only name, hidden
New/Load, open-sample behavior, active-operation and training-busy gating, and
threshold persistence after reopening Machine.db without changing training
samples. It also renders both toolbars at 1920x1080. No production DB or physical
device was used.
Final checks passed: 140 Virtual tests, both off-screen WPF workflows and actual
synthetic CPU training/reinspection. Release solution and Virtual app builds had
zero warnings/errors.

### Inspection teaching and Data Matrix — 2026-09-08

This supersedes the preceding Training recipe-toolbar/tuning design. Operating
capture, bolt ROI/threshold tuning and recipe saving now belong to Station Teaching;
Training has no motion commands or editable operating thresholds. Its own labeling
ROI, database operations, validation and training remain in the training project.

The carrier mosaic and camera pane remain visible together. FOV is read-only and
follows XY feedback: Move & Inspect moves to the selected bolt or barcode center,
not to a separately taught FOV position. PCB 1/2 each have one Data Matrix rectangle,
saved in the recipe, with Esc cancelling an unfinished drag. Original-resolution
barcode crops use the carrier image's mm/px scale; they bypass U-Net resizing.

Automatic read failure stops with an Inspection alarm for operator confirmation.
Virtual images contain real decodable Data Matrix symbols; missing bolts and
unreadable barcodes are separate scenarios. Tests cover central crops, inverted
symbols, row padding, manual center movement, failed-read stop/reset/restart,
recipe persistence and existing full-equipment flows. All 144 Virtual tests pass.
Off-screen WPF checks pass with no binding errors, including barcode teaching and
reading. Actual synthetic CPU training/reinspection also passes. Release and Virtual
builds have zero warnings/errors. Real-camera readability remains a commissioning
check. See [Inspection Teaching](INSPECTION_TEACHING.md) for the current workflow.

### Shared PCB pattern — 2026-09-08

PCB 1/2 now share one PCB-local bolt list and Data Matrix region in `Recipe.Pcb`.
Teach PCB 1 bounds once; place PCB 2 by its corresponding upper-left corner.
Changing dimensions does not scale the pattern. Both camera and fastening targets
read the same definition and the selected PCB origin; results remain per PCB.
The existing backup-plate and head reference-pin transforms remain unchanged.

Verified shared edits, two-location motion, head transforms, DB save/reload,
per-PCB process results, and full equipment flows: all 145 Virtual tests pass.
Release and Virtual builds have zero warnings/errors. Off-screen WPF rendering
and barcode preview pass without binding warnings or physical machine commands.
Old duplicated coordinate recipes require PCB-pattern teaching, not guessed conversion.

### PCB teaching coordinate and FOV review — 2026-09-08

Removed the separate physical camera FOV settings. Camera frame dimensions and
recipe mm/px now determine scan pitch, barcode bounds and the displayed FOV.
Hik reads Width/Height once during initialization; UI feedback never polls the SDK.
Changing mm/px refreshes the barcode preview crop and clears its old decode result.

Image clicks/drawn regions select their containing PCB before applying the shared
pattern. Bolt and barcode rows display PCB-local centers while motion still reads
machine coordinates. Static recipe/image editing works before Home in idle Manual;
motion, capture and IO retain their original interlocks.

Extended the existing regression tests rather than adding another test project.
All 145 Virtual tests pass, including finer-scale scan coverage, PCB 2 clicks while
PCB 1 is selected, local coordinate labels and unchanged hardware command gates.
Release/Virtual builds: zero warnings/errors. Off-screen 1920 x 1080 WPF rendering,
barcode preview and binding checks pass; no physical machine commands were issued.

### Recipe save and teaching transitions — 2026-09-08

Reproduced and fixed four normal UI paths in an isolated Virtual setup:

- Rescanning with the same selected bolt clears its old captured image and OK/NG.
- Point/region autosave and Save use one name condition; empty names create no rows.
- Injected SQLite save errors stay at RecipeEditor and appear in the shared shell
  notification. The edited values remain in memory, the existing DB transaction
  keeps stored data intact, and retry saves the edit without reconstructing it.
- New/Load resets the PCB selection, so a new recipe can begin with PCB 1's region.

Existing recipe tests also cover image-save rollback, cancellation and failed
last-selection persistence. The WPF check verifies that the shared error is visible
when set and takes no layout space when cleared. No per-point exception handlers,
recipe snapshots or physical hardware calls were added.

### Follow-through review of teaching and preview — 2026-09-08

Followed the previous fixes through position edits, reinspection, machine-position
saving and recipe loading. An isolated Virtual/WPF probe reproduced four more paths:

- Reteaching the same selected bolt retained the old image and judgment. Position
  refresh now clears the capture centrally, including shared PCB region edits.
- Cancelling reinspection, then changing thresholds, resurrected the old prediction.
  Preview prediction reset is shared by capture, clear, ROI edits and reinspection.
- Machine-position storage exceptions escaped the teaching command. The common
  teaching base now reports storage failure in the shared motion panel, retains edits
  and allows retry; a failed pin save no longer advances selection.
- A completed recipe read could still replace the recipe after cancellation while
  waiting for the UI continuation. Load checks cancellation immediately before apply.

The last case was reproduced by holding the UI continuation until after cancellation,
not by slowing or postponing cancellation in production. Existing regression coverage
was extended without adding a test project. No rollback snapshots or new hardware
interlocks were introduced.

Rechecked the connected paths after these fixes: all 145 Virtual tests pass,
including the existing teaching regression extended for reteaching and cancelled
reinspection. The off-screen WPF check verifies the shared save-error display for
both Supply and Station Teaching, with no binding warnings. Release and Virtual
builds pass with zero warnings/errors. The first concurrent solution build hit
testhost DLL locks; the sequential rebuild passed after the tests finished.

### Model threshold ownership — 2026-09-08

Mask Threshold now belongs to BoltTrainingSettings and Training.db. Epoch/batch/
optimizer settings stay there as before. Minimum Mask (%) remains in the inspection
recipe and Station Teaching, along with ROI and acquisition conditions. Recipe
New/Load never changes the shared model threshold.

Bolt Training exposes the mask threshold beside Max Epochs and saves it through
Save Settings without retraining. Existing review probabilities refresh immediately.
Automatic inspection receives a threshold getter at composition time, so Inspection
does not acquire a reverse project reference to Training. Preview, model review and
automatic judgment use the same setting; no per-recipe threshold copy remains.

Verified with all 145 Virtual tests, off-screen WPF binding/layout checks and actual
synthetic CPU training plus saved-model review. Model-threshold edits update existing
review masks without modifying weights; recipe area edits do not change the model
threshold. Both DB roundtrips and recipe replacement preserve this separation.
Release and Virtual builds pass with zero warnings/errors.

### Follow-through review of model validation — 2026-09-08

Reproduced two connected issues using the existing isolated CPU/WPF harness:

- Reviewing a saved model unnecessarily required a complete training dataset and
  loaded training images. Review now prepares only included, labeled validation
  images; one is sufficient. Train keeps its existing dataset requirements and
  reuses prepared validation inputs for its final review.
- A failed operational model reload retained the previous weights, allowing
  CheckReady to succeed against stale state. Reload now releases the old model
  first. Failure leaves it unloaded, so the existing automatic-start readiness
  check reports the model error instead of using the old weights.

Verified a single validation image with all training images excluded, an empty
validation set, failed reload followed by CheckReady, and recovery after restoring
valid weights. No model files, rollback snapshots or new exception layers were
introduced. Empty validation clears the previous result through the existing UI
error boundary without modifying the stored model.

All 145 Virtual tests, actual synthetic CPU training/review, off-screen WPF binding
checks and the isolated recipe/teaching cancellation-and-save probe pass. Release
and Virtual builds have zero warnings/errors. These checks verify software flow,
not real-image inspection accuracy; no physical hardware was called.

### Station 3 operating display

The operating badge now names the actual inspection/transfer step rather than
only Working. The current heat sink and bolt appear together; the same heat-sink
pocket is highlighted on the shared carrier view. Transparent camera geometry
keeps the active bolt marker visible beneath its center.

PCB barcode rows below Station 3 read the current production assemblies, disappear
with their carrier/heat-sink inputs, and are hidden when inspection is disabled.
Inspection publishes changes when each barcode or bolt result is recorded, rather
than waiting for another motion event. Completed work distinguishes rear-SMEMA
waiting from NG-shuttle waiting. Operator-facing alarm text shows only the error
message; the full diagnostic exception remains available in the tooltip.

Verified with an off-screen operating view driven by the actual Virtual automatic
loop and the recipe/model from the first-use teaching workflow. The rendered camera
center matches the active bolt center; barcode/result bindings, NG waiting and
barcode-failure text pass without binding warnings. Synthetic model judgments are
only flow checks, not a production-accuracy assessment.

The follow-up probe retains one operating view for the entire run, so rebuilding
the view cannot hide missing change notifications. It checks the rendered status,
barcode contents/visibility and alarm detail through Stop, restart, carrier
replacement, alarm Reset, NG storage and rear-SMEMA discharge.

This exposed an empty-carrier display precedence error: Station 2 and Station 3
must check completed work before showing Empty Carrier. A completed empty carrier
now reports its transfer wait; Station 3 identifies Waiting for Shuttle when routed
to NG. No controller states, extra interlocks or exception paths were added.

The isolated probe holds upstream availability OFF to prevent Virtual's automatic
carrier replenishment from overlapping the manually placed Station 3 scenarios.
A known OK result also exercises rear-SMEMA waiting and exit-sensor discharge
independently of the synthetic one-epoch model's classification. Both paths update
the existing WPF controls without binding warnings.

Follow-up validation: all 146 Virtual tests pass; Release and Virtual builds have
zero warnings/errors. Physical hardware and production databases were not used.

### Station 2 shooting and manual tube clearing

Stop/restart was exercised at bolt arrival, escape retraction and head lowering,
with both ordinary and short tube-sensor pulses. The loaded bolt is not shot again,
and the head lowers only after loading, tube clearance and escape retraction.
Feeder waiting is a separate state; when the feeder becomes ready the station
rechecks the current head/tube inputs before issuing a loading cycle.

The owner exposes Shoot Bolt as a momentary teaching output. The common IO template
renders HOLD instead of ON/OFF and uses the existing cancellable output command.
The gantry owns air-OFF cleanup; no feeder/escape/vacuum command is issued by manual
shooting. Teaching operations cancel when Manual mode ends, as well as through
their existing release, navigation and Stop paths. No homing prerequisite was
added to manual air control. Alarm Reset and ordinary manual-output readiness
still apply.

Validation: all 147 Virtual tests pass, Release/Virtual builds have zero warnings
or errors, and the off-screen WPF teaching probe verifies the HOLD button's
visibility, scroll access and press/cancel bindings without binding warnings.
The command tests cover release, group change, screen exit, Stop and Manual-to-Auto
cancellation. No physical shooting or equipment commands were issued.

### IPM bolt pickup order

Head 1 pickup now moves Safe Z -> pickup XY -> cylinder Down confirmation ->
pickup Z -> feeder-ready wait -> vacuum ON confirmation. The agreed retreat is
vacuum held ON -> Safe Z -> cylinder Up -> fastening XY. The former combined XYZ
pickup move and its raise-before-vacuum helper were removed. New state entries
distinguish cylinder lowering, Z approach, feeder waiting and loaded-head raising.
The pickup Z value must be taught with the head cylinder lowered.

The existing two-heat-sink fastening test checks the cylinder/Z/vacuum order and
stops immediately after pickup. Restart retains that bolt, and both IPM seating
bolts are independently picked while both final passes reuse installed bolts.

All 9 fastening tests pass. Release and Virtual builds have zero warnings/errors;
no physical hardware was operated.

### Both fastening heads use the shared Safe Z

Both heads now move between fastening points in XY at the machine-taught Safe Z.
Their cylinders provide the working stroke; only IPM feeder pickup and its return
need common-Z travel during the cycle. The shared bolt pattern no longer stores Z,
and automatic readiness no longer requires a per-bolt height.

Station 2 keeps the calculated bolt XY list for Move To verification. These are
read-only targets from the inspection PCB pattern and head locating pins, not
duplicate teaching points. Removed the special first-Z/Move-XY command and button;
bolt definitions are edited in Inspection. Safe Z and pickup XYZ remain machine
teaching positions. Manual head-down Jog/Step behavior is unchanged.

Existing tests now use a nonzero Safe Z and verify that the gantry only leaves it
at pickup XY, across both PCB bolts, IPM seating/final passes, and Stop/restart.
All 147 Virtual tests pass; Release and Virtual builds have zero warnings/errors.
The isolated off-screen WPF probe passed without binding warnings, and the rendered
Station 2 page shows calculated targets without the obsolete Z-teaching button.
No physical machine was connected or operated.

### Manual pickup Move To matches the automatic approach

Bolt Pickup no longer uses a generic XYZ move with the head raised. Its teaching
command calls the gantry's pickup approach: raise cylinders, Safe Z, pickup XY,
Head 1 Down feedback, then pickup Z. The automatic state's individual actions use
the same gantry methods; its feedback-based state selection remains intact.
Neither vacuum output nor feeder readiness is part of manual positioning.
Removed the unused generic fastening XYZ entry point and the station's duplicate
pickup-XY helper. The point's teaching hint now states that Move To lowers Head 1.

The new focused Virtual test with manually driven DI confirms that Z waits at
Safe Z until Down feedback arrives. Stop during that wait prevents the Z move,
retains pneumatic outputs, and a retry completes the taught pose without changing
vacuum. The existing automatic pickup/restart, head-down adjustment and horizontal
interlock tests also pass: 12 focused tests total. Release and Virtual builds have
zero warnings/errors. No physical equipment was operated.

### Recursive pickup follow-up — return, feedback timeout and UI

Added a gantry-owned Return from Pickup action to Station Teaching. It uses the
existing Safe-Z and Head-1-Up operations in that order, without XY movement or
vacuum changes. The command stays available after selecting another fastening
point, but remains subject to ordinary homed/manual readiness. It shares Stop,
mode/group/page change cancellation and shutdown draining with the other manual
teaching commands. No new automatic recovery state was added.

Following the new action into its failure path exposed an existing omission:
teaching IO buttons handled IoTimeoutException, but the pickup Move To sequence
did not. Both entry and return timeout tests first reproduced the escaping
exception. Moved the existing owner-alarm handling into the common teaching
execution boundary instead of adding catches to individual commands. The
backup-plate buttons retain their explicit Main Conveyor alarm ownership.

The manual pickup test now also cancels return exactly when Z reaches Safe Z:
Head 1 must not rise after that cancellation. Retry waits for real Up feedback
and leaves XY/vacuum unchanged. The off-screen WPF check verifies the return
button is visible only for fastening, disabled before Home, and fully accessible
inside the motion panel. No binding warnings or additional actionable issues
were found in the subsequent pickup-path review. No physical equipment was run.

Final verification: all 150 Virtual tests pass; Release and Virtual builds report
zero warnings/errors, and diff whitespace checks pass.

### Shooting preparation — resume from escape feedback

The existing shooting-loading action retracted and advanced the escape on every
entry. A stop after advance exposed the wrong next state: it waited for another
feeder bolt although the escape was already forward. The Virtual regression
first failed with WaitingForShootingFeeder instead of the shooting state.

Split escape advance from shooting. Backward feedback requires feeder readiness;
an intermediate escape position completes the forward stroke. Forward feedback
selects ShootingBolt without refeeding or retracting first. No saved step or
completion flag was added. Vacuum ON, passage monitoring, shooting air and arrival
confirmation stay together so a short tube-sensor pulse is not lost between states.
Existing tube-clear/operator-clearing behavior and air-OFF cleanup are unchanged.

Extended the existing fastening test rather than adding another scenario suite:
Stop during advance, delayed forward feedback while stopped, restart with feeder
detection OFF, and no escape reversal before the first shot. Existing arrival,
escape-retraction and head-lowering stops still cover both normal and short tube
pulses, the two pickup passes, results and cylinder/XY ordering.

Verification: all 150 Virtual tests pass, Release and Virtual builds report zero
warnings/errors, and diff whitespace checks pass. No physical equipment was run.

### Recursive fastening follow-up — movement and passive waits

Following the shooting states into their callers found a second escape-retraction
path inside MoveToBoltAsync. It could undo the prepared escape after a stopped
operator repositioned X/Y. Removed that hidden IO action and reused the gantry's
existing X/Y move, including its raised-cylinder check and Safe-Z behavior. Tube
clearance now takes priority over moving back to the PCB bolt. The existing flow
test reproduced both wrong movement priority and escape reversal before the fix.

Following the wait states found that they awaited only feeder-ready DI. Delayed
pickup-vacuum or escape-forward feedback could change the correct state without
waking that wait. The station now subscribes to its work, gantry and feeder
notifications and re-evaluates passive feeder waits through the existing
AsyncAutoResetEvent. Subscriptions end with each run; repeated events coalesce.
Active commands remain sequential and keep their own feedback/timeout waits.
Deleted the unused feeder WaitUntilReadyAsync wrapper. Supply timeouts still
belong to the independent feeder loops.

One focused two-case regression keeps both feeder DIs OFF and manually applies
late vacuum/escape feedback. Both cases stalled before the fix and now continue
to the appropriate head action. The existing full fastening scenario also checks
manual repositioning between Stop and Start without refeeding. Subsequent review
covered pickup retreat, cylinder/XY order, pass transitions, ADC stop cleanup,
result recording, carrier completion and event unsubscription.

Verification: all 152 Virtual tests pass, including 11 fastening/feeder tests.
Release and Virtual builds have zero warnings/errors; diff whitespace checks pass.
No physical equipment was connected or operated.

### ADC completion at the Stop boundary

The full fastening regression now cancels result reception after the Virtual ADC
has physically completed its first PCB bolt. Before the fix, the result collection
was empty when the station stopped, leaving that completed bolt eligible for a
repeat. AdcBoltHead now sends Stop first and, only when cancellation left a pending
event without a received result, reads the latest result once. A new completed
event returns through the original FastenAsync call and its captured assembly,
bolt number and pass. Otherwise cancellation propagates normally.

No cancellation delay, restart command, polling loop, pending-job cache or extra
state was added. The one post-Stop read uses the bus response timeout rather than
the already-cancelled operation token; Stop completion can wait for that response.
The next station action still checks the cancelled token before doing any work.

The existing flow test verifies Stop precedes this single read, only one fastening
Start occurred before the stop, the first torque-NG result is retained, and restart
still executes exactly the original six fastenings across both heat sinks and
three passes. The incomplete-cancellation test now begins with a previous success
to prove that result is not reused. Existing carrier-replacement coverage confirms
the result stays on the original assembly rather than the replacement carrier.

Verification: all 152 Virtual tests pass without adding a new test case. Release
and Virtual builds report zero warnings/errors, and diff whitespace checks pass.
No physical equipment was run; ADC timing remains to be checked on the machine.

### Shared automatic-loop execution

NgShuttle and BoltFasteningStation now use the small AutoUnit base in IBTM.Core.
It owns sequential repetition, the coalescing change signal, run-lifetime event
subscription and normal run cancellation. Each unit still selects its own enum
state and executes its own actions. Waiting states await the shared signal;
completed actions immediately re-evaluate current feedback, even when the enum
has not changed. Events never start another action while one is running.

The base does not own hardware Stop, alarms, recipes or carrier state. Motion and
IO calls still receive cancellation tokens and their device owners stop them.
Fastening retains its carrier-scoped cancellation, target selection and result
cleanup; shooting air is still stopped in its own finally block. Only cancellation
of the applicable run/carrier token is treated as normal completion. Other errors
continue to the existing alarm boundary. Other automatic units are unchanged.

Three focused cases cover event coalescing without overlapping actions, waiting
cancellation/restart and propagation of unrelated cancellation or other errors.

Verification: all 155 Virtual tests pass. Release and Virtual builds report zero
warnings/errors, and diff whitespace checks pass. No physical equipment was run.

### Shared execution applied to all automatic units

The remaining Main Conveyor, PCB Supply, PCB Placement, both Bolt Feeders,
Inspection / NG Carrier Transfer and NG Conveyor now use AutoUnit. Its scope is
unchanged: sequential repetition, coalesced change waiting and run cancellation.
No generic state/recipe framework, lifecycle hooks or new physical-state cache was
introduced. Every unit still exposes its original RunAsync arguments.

Preserved unit-specific behavior:

- Supply's PCB 1/2 selection resets on Start. Its existing upstream-exit event
  handling stays run-local and runs before notifying the common wait, preserving
  short SMEMA transitions during an active handoff. Upstream READY is cleared on
  exit.
- Placement keeps its current carrier's selected heat sinks until completion or
  carrier removal and reselects after Stop. All gripper/lift/vacuum action orders
  are unchanged.
- Main Conveyor keeps its operation scope, immediate token-owned motor Stop,
  SMEMA cleanup, downstream priority and interrupted-transfer direction.
- NG Conveyor keeps token-owned motor Stop, eject-button release handling,
  compaction and output cleanup. NG Shuttle behavior is unchanged.
- Each feeder still times out only while waiting for a bolt. Once ready, it stops
  feeding and uses the common change wait; its event remains filtered to its own
  bolt DI. Feeder errors propagate and feeding is stopped in finally.
- Inspection still restarts incomplete inspection on Start. Its per-carrier
  cancellation remains local, like fastening; unrelated cancellation is no longer
  swallowed by that inner scope. NG Transfer uses the same gantry loop.

Condition waits inside BufferStage, camera/ADC/device polling and per-carrier
inspection/fastening loops were not forced into independent automatic units.
The existing MachineController still owns enablement, shared run cancellation and
unit-alarm reporting. A focused common-runner regression additionally checks that
cancellation at action completion and an already-cancelled Start execute no next
action, even with a pending change notification.

Verification: all 156 Virtual tests pass. Focused conveyor/inspection/fastening
and supply/placement/lifecycle runs also passed before the final suite. Release
and Virtual builds have zero warnings/errors; diff whitespace checks pass. The
automatic state-action lists were compared before and after migration, with no
missing state branches. Including AutoUnit, production code is 151 lines shorter
than before the two-unit trial; test/documentation additions are excluded.
No physical equipment was operated.

### Automatic unit startup supervision

Reproduced an immediate Main Conveyor failure on its first READY output: the old
startup code still turned on the subsequent shooting feeder before observing the
failed conveyor task. The regression holds upstream AVAILABLE OFF so this output
is issued before the conveyor's first asynchronous hardware wait.

MachineController now observes each unit as it starts. Its existing local start
function skips disabled units and units whose shared run was already cancelled.
The local observer reports an unexpected exit/failure, preserves an existing run
alarm and cancels the shared run. The controller then awaits all started units;
the Task.WhenAny/tuple lookup is removed. No new controller class or per-action
guard was added, and the units still run independently.

The startup regression covers both operator Stop and an injected failure. It
checks that the later feeder never turns on, the original error is retained, and
operation scopes drain. The existing feeder timeout/restart regression now runs
Main Conveyor alongside the feeder and checks both units clean up while retaining
the feeder alarm. An automatic fastening regression holds a head's Stop completion
open: the machine remains running and cannot Reset until cleanup ends, and a late
head-cleanup failure does not replace the original air-pressure alarm.

Review also covered hardware readiness cancellation, synchronous start-delegate
failure, inspection/transfer enablement, normal Stop without an alarm, and
hardware fault/reset paths. Hardware safety-alarm priorities were not changed.

Verification: all 158 Virtual tests pass. Release and Virtual builds report zero
warnings/errors, and diff whitespace checks pass. No physical equipment was run.

### Manual execution and shared motion display

Manual Conveyor mode-change Stop now runs in MachineController's input handler,
not a queued UI refresh. A regression verifies it without constructing a view.
Individual-axis Home admission, execution and servo dispatch also moved out of
ManualHardwareViewModel. Manual Home and teaching share cancellation monitoring
and IO/motion error handling in the existing controller. Physical movement and
Stop remain owned by each unit/device; no new controller framework was added.

The review reproduced a Jog lifetime problem in Supply, Placement and Inspection:
their synchronous start returned before motion ended, disposing the teaching
cancellation scope too early. The shared manual-motion boundary now retains the
scope until moving feedback turns OFF. Cancellation still reaches the device
immediately; only command completion waits for the device to stop. Regressions
cover release and Manual-to-Auto mode changes for all three handlers.

TeachingOutput carries an explicit HardwareArea owner supplied by its unit.
The controller maps that owner to a machine alarm; teaching UI no longer identifies
backup-plate output names. Existing backup-plate timeout coverage still confirms
Main Conveyor attribution, and individual Home failures retain Home Failed.

Manual and teaching coordinate text now binds to each unit's shared MotionStatus.
The separate coordinate copies and manual-axis dispatcher queue were removed.
Only the inspection FOV overlay keeps a coalesced UI update. The three side-view
Z scales share a XAML template with centered rails and carriages.

Verification: all 162 Virtual tests pass. Release and Virtual builds have zero
warnings/errors; diff whitespace checks pass. Production code is approximately
190 lines shorter, excluding tests and documentation. No physical equipment was
operated, and this pass did not perform an on-screen visual inspection.

### Follow-through: teaching capture, device stop and raw DO

Carrier scans and bolt/barcode capture still used their own linked-token scope.
They now use the same manual-motion boundary as Move/Jog, so changing Manual to
Auto cancels movement without waiting for a UI refresh. Carrier capture, bitmap
conversion, saving and point selection stay inside that boundary; cancellation
does not fall through into saving or advancing. Camera errors remain local to
the teaching screen; motion faults use the controller's existing alarm handling.
Two regressions cover carrier scan and single-point capture cancellation, then
successful recapture after returning to Manual.

Ajin's SDK declaration in AnyWave/AnyWave.Device/Motions/Ajin/AXM.cs identifies
AxmMoveSStop as deceleration stop. Jog, positioning and Home now retain their
motion lifetime until AxmStatusReadInMotion reports OFF. Cancellation sends Stop
immediately; only cleanup waits. This wait uses MachineOptions.TimeoutMilliseconds
and does not require InPosition, since a fault-stopped axis can lack that signal.
Status-read failures or stop timeouts become motion errors rather than successful
completion. Jog startup and monitoring share one cleanup path. A focused driver
test covers delayed OFF, stopped-with-fault/no-InPosition and stop timeout using
injected status responses; actual RTEX deceleration remains a commissioning check.

Raw DO controls no longer rely only on MainWindow closing after a queued state
notification. MachineController checks manual-output admission at the write
boundary, and write failures enter the existing IO communication alarm path.
The diagnostic window still observes mapped feedback without becoming a unit
sequence. A regression verifies that an existing row cannot write in Auto or
during another operation, and can write again when manual controls are available.

Verification: all 166 Virtual tests pass; the two capture tests additionally pass
with restart assertions. Virtual build and diff whitespace checks pass. No new
project, wrapper class or physical-state flag was introduced. No equipment was
operated and no on-screen visual validation was performed in this follow-through.

### Unit-owned action completion

The manual-motion boundary's MovingChanged wait was a workaround for void Jog
commands. It is now removed: IAxisMotion exposes JogXAsync/JogYAsync/JogZAsync,
and Supply, Placement and Inspection return those tasks through JogAsync. The
device owns completion, including cancellation cleanup and final feedback.
Fastening keeps its existing bounded adjustment behavior and Task contract.

Ajin Jog reuses the existing positioning execution/stop path instead of a separate
fire-and-forget monitor. Virtual Jog also returns its running task. The unused
motion Faulted event and controller subscriptions are removed; command failures
travel through the awaited task, and the manual boundary reports motion alarms
and stops run outputs. Hardware axis alarm/readiness checks are unchanged.

BoltInspector owns move-then-capture for both bolt targets and PCB barcodes.
Teaching selects the target and displays the returned image; it no longer composes
motion followed by camera capture. No new service, project or wrapper was added.

The existing tests now await Jog cancellation/failure, verify that task completion
waits for final feedback, and exercise error/reset/restart through the manual
command boundary. Inspection coverage directly captures a barcode and bolt without
a ViewModel and verifies their taught positions and image results. All 166 tests
pass, including the additionally strengthened inspection test; no test cases were
added. This supersedes the MovingChanged workaround described above.

### Broader responsibility review

Reviewed the project-reference graph (19 projects, including tests), automatic
unit entry points, UI movement dispatch, synchronous manual commands and delayed
Virtual responses. There are no project-reference cycles or references from
lower production projects back to the WPF application. Automatic units continue
to use AutoUnit; unit-specific state decisions and stopping remain independent.
Virtual DO-to-DI delays are simulated hardware responses, not operation completion
tasks, so they were not removed as if they were the former detached Jog commands.

Supply, Placement and Fastening now own MoveToTeachingPositionAsync. The two
teaching screens no longer duplicate Placement movement dispatch, and Supply's
horizontal-then-Z sequence no longer lives in the ViewModel. Existing teaching
metadata and axis coordinates are passed directly; no UI type or new service is
introduced in a lower project. TeachingPoint.Read supplies the editable values,
including staged buffer coordinates, without applying them to stored settings.

Synchronous raw DO writes, manual Conveyor Run and Servo toggling share one
admission/error boundary in MachineController. Manual Conveyor Run rechecks
permission when invoked, and the Servo toggle's hardware read/write calls report
failures as motion alarms. The separate UI status refresh remains the read-side
observation below. The underlying unit still
performs the hardware action. Tests exercise a stale Conveyor command in Auto,
an injected servo feedback failure, and direct handler teaching with a staged Z
value while preserving the stored handoff Z and Supply Y-before-X order.

Remaining read-side observation: ManualAxisRow.RefreshState still reads
IMotionFeedback.GetAxisState during a UI refresh. A follow-up should move status
acquisition into the shared unit status path, with explicit hardware-read failure
handling. This pass does not add per-row exception swallowing or fabricate a
healthy fallback state, and does not claim measured RTEX/UI read performance.

Verification: all 167 tests pass. A Release build attempted alongside the test
runner hit locked output DLLs; sequential Release and Virtual builds after the
tests completed both report zero warnings/errors. Diff whitespace checks pass.
No physical equipment or on-screen UI session was operated for this review.

### Recursive responsibility follow-through — 2026-09-08

- Moved per-axis display feedback and its condition enum into Device.AxisStatus,
  owned by each unit's existing MotionStatus. ManualAxisRow now holds references
  only; both the row-state cache and RefreshRows were removed. XAML binds directly
  to the shared axis object. Operation position-known indicators use the same
  display feedback. Hardware read failure invalidates the displayed group and is
  rethrown to the caller, rather than leaving a healthy-looking row.
- Ajin and Virtual publish initial axis state. The controller also refreshes
  display feedback on safety/servo-power input changes, after stopping when
  required, and when the manual or operation screen is activated. No background
  motion polling or simulated UI motion was introduced.
- Display state is not motion authority. Existing GetAxisState-based home,
  movement and automatic-operation checks remain live device reads. The operation
  screen uses ReadAvailability to reuse one readiness read for its home display,
  start-block reason and Start/Home buttons; actual commands read again. This
  reduces repeated reads, but does not claim that all SDK reads have moved off
  the UI thread or that every read-side failure path has been redesigned.
- Each handler now owns CanJog. Supply owns teaching-position rotation and
  buffer conditions as well. Teaching screens select the unit and forward its
  result instead of duplicating mechanical rules. Fine adjustment on the bolt
  gantry remains distinct from cylinder-up automatic horizontal travel.
- OperationCancellation.Link now releases a newly registered scope if its
  activity notification throws. Otherwise shutdown could wait forever for a
  scope the caller never received. Manual execution now includes scope creation
  and disposal in its cancellation/error boundary; an already cancelled or
  shutdown request does not escape to an async UI command.

Follow-through checks cover shared display identity, no SDK reads from row
properties, unknown feedback on read failure, refresh/recovery, home and servo
updates, failed scope creation followed by restart/shutdown, and a stale manual
command after shutdown. Existing Jog, door/reset, teaching and equipment-flow
tests are retained; no physical hardware was actuated.

Final verification: 170 tests pass. Release solution and Virtual application
builds both complete with zero warnings/errors. The 19-project reference graph
has no cycle or lower-production-project reference to the WPF application.
Diff whitespace checks pass. This is code/Virtual validation, not a new visual
inspection or a real-equipment commissioning result.

### Display acquisition boundary — 2026-09-08

This follow-through supersedes the synchronous display-refresh and
ReadAvailability notes above. MachineState now owns one event-driven display
worker. Hardware/process notifications leave one pending refresh request; the
worker refreshes the shared MotionStatus axes and publishes a completed
MachineDisplay. There is no new timer, per-event Task.Run, device wrapper, or
control-state cache.

OperationViewModel no longer queries the placement/fastening/inspection state
machines or hardware availability to display them. Those reads run in the
controller's background display capture. Manual axis rows still bind the shared
AxisStatus objects; their construction, state getters and Home CanExecute do not
read the SDK. Opening either display requests an update rather than performing
hardware reads on the UI thread. The main status header remains subscribed when
the operation page is inactive. Small visual projections (bolt markers, layout,
property notifications) remain UI work and are coalesced before rendering.

MachineDisplay is observation only. Start, Home, individual-axis Home, Servo and
Conveyor commands retain their actual admission checks. Existing unit motion
interlocks and teaching-command/position checks are not replaced by displayed
values. This is not a claim that every teaching CanExecute or command-time native
read has been moved off the UI thread.

A failed display read invalidates the affected axis display and publishes Status
Unavailable with the error. It does not fabricate healthy feedback or create a
physical machine alarm solely because a display read failed. Control-side
hardware/safety failures retain their existing stop and alarm owners. A later
successful notification-driven read restores the display.

The display worker starts after hardware initialization releases its operation
scope, continues after machine Stop, and is cancelled/joined on application
shutdown or service disposal. Tests cover a blocked read with 1,000 coalesced
requests, non-blocking manual-view reads, Stop versus shutdown, failed-read
recovery, and live Home admission despite an earlier available display. Existing
home-display tests now await the published display instead of assuming hardware
events synchronously repaint the UI.

Verification: 172 tests pass; Release solution and Virtual application builds
have zero warnings/errors. No physical hardware or visual UI session was used.

### Twenty follow-through passes — 2026-09-08

The requested twenty passes were a bounded inspect/change/recheck cycle, not
twenty scheduled tasks or twenty forced code changes. The scope started at the
display exception boundary and followed its actual callers and dependencies.

| Pass | Follow-through | Result |
| --- | --- | --- |
| 1 | Recoverable display failures | Only IOException is recovered within the refresh loop. |
| 2 | SDK error sources | Ajin/AlphaMotion return-code failures use IOException; programming errors remain distinct. |
| 3 | Error information | Keep the original exception; UI message and diagnostic details use Message and ToString respectively. |
| 4 | Initial refresh failure | Fail/cancel the initial completion rather than marking a failed worker ready in finally. |
| 5 | Later refresh failure | Show a terminal worker error and rethrow; no automatic retry of programming errors. |
| 6 | Shared axis notifications | Notify XyHomed only when it actually changes; failed hardware reads still invalidate the group. |
| 7 | Axis inventory | Remove the duplicate motion array; control checks still use live Feedback. |
| 8 | Individual-home display | Reuse sampled axis states for display; actual Home admission reads hardware. |
| 9 | NG conveyor display | Read motor output/state during display capture instead of in operation-screen getters. |
| 10 | Synchronous manual admission | Evaluate permission inside the existing command/error boundary. |
| 11 | Async manual/individual Home | Share live admission before registering the operation; preserve unit-specific cancellation checks. |
| 12 | Teaching Step | Read the actual step origin inside the command boundary; preview range calculations use display position. |
| 13 | Runtime recipe access | Resolve the recipe once, then read its current properties; eliminate repeated DI service lookups. |
| 14 | Virtual/lifetime follow-through | Capture VirtualIoService once. Removed disposal-time service lookups rather than catching ObjectDisposedException. |
| 15 | Teaching command notifications | Coalesce pending dispatcher refreshes without a timer or per-event task. |
| 16 | Camera cancellation | Remove duplicate outer cancellation swallowing; normal motion cancellation remains owned by the common command boundary. |
| 17 | Required cleanup | Retain AutoUnit cancellation filters, operation draining, serial-bus release and shooting cleanup; these perform real cleanup, not recovery guesses. |
| 18 | Unused state and dependencies | Remove two unused buffer display fields and their reads. All 19 projects remain acyclic with no production reference back to WPF. |
| 19 | Regression checks | Add only startup/runtime programming-error, pre-home read-error and live-recipe lifetime regressions; run the full suite. |
| 20 | Final cross-check | Re-read modified call paths, combine identical manual hardware-error handling, preserve CTS cleanup on worker failure, rebuild both configurations and rerun the suite. |

Narrowing the exception boundary exposed four disposal-time failures in the
Virtual integration run: recipe delegates still resolved services from a disposed
provider. Capturing the live recipe/settings/IO objects fixes the ownership
issue without retries, shutdown-specific exception suppression, or cloned
recipes. Recipe.ReplaceWith continues to replace child objects, so delegates
read current properties rather than retaining an old PcbLayout.

The broad catch at the worker's outer lifetime boundary is intentionally
terminal: it publishes the exception and rethrows it. It does not turn arbitrary
code errors into successful refreshes. Actual equipment control, hardware
fault-stop boundaries, and cleanup finally blocks remain separate from this
display-only policy. This review does not claim to remove every UI-thread native
read, and it does not alter the mechanical sequence or replace live safety checks
with display data.

Verification: 176 tests pass; Release and Virtual builds have zero warnings and
errors. No physical hardware was commanded and no new UI visual session was run.
