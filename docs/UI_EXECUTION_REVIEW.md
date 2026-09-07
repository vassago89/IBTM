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

Outside the UI-only audit, the two unanswered mechanical questions remain open:
whether a Station 2 head change needs common-Z retraction before cylinder movement,
and whether a stopped shooting cycle with tube detection ON/head vacuum OFF may
resume air without feeding another bolt. Neither behavior was guessed or changed.
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
vacuums; NG Transfer has pickup lift and gripper. Conveyor, shuttle, feeder and
shooting sequence outputs remain read-only in these teaching panels.

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
groups. Fastening teaching groups each head's pins and bolt heights together,
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
