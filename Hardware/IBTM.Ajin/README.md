# AJINEXTEK SDK declarations

These manufacturer-supplied C# files are copied byte-for-byte from the AnyWave
reference project, including their original encoding and declarations:

- `AXL.cs`: `AnyWave.Device/Ajin/AXL.cs`
- `AXHS.cs`: `AnyWave.Device/Ajin/AXHS.cs`
- `AXM.cs`: `AnyWave.Device/Motions/Ajin/AXM.cs`
- `AXD.cs`: `AnyWave.Device/IOs/Ajin/AXD.cs`

The .NET SDK includes these files directly in `IBTM.Ajin`. Application code calls
`CAXL`, `CAXM` and `CAXD`, and uses the manufacturer's `AXT_FUNC_RESULT` definitions.
Do not introduce duplicate P/Invoke declarations or edit the vendor files; replace
them together from the manufacturer when updating the SDK.

The native `AXL.dll` and `EzBasicAxl.dll` are copied to the output directory.
`AXL.lib` is retained as an SDK file but is not copied for this C# application.
Copying these files does not install or initialize the native driver.
IBTM retains its configured interrupt number, axis scaling and RTEX module mappings.
No motion-parameter file path or .mot file is required by this application.

## DIO initialization and scanning

The manufacturer's `Visual C#/DIO/DigitalIO/FormDigitalIO.cs` sample opens AXL
with `AXT_RT_SUCCESS == 0`, queries the DIO modules, and reads inputs in WORD units.
IBTM uses `AxlOpen`, then checks DIO presence, module count, and each configured
module's identity and DI/DO counts, matching that DIO sample. It does not call
`AxlOpenNoReset` or `AxmMotLoadParaAll`. The open result is logged.
An open failure returns directly; a module failure closes the opened library
and preserves any cleanup error.
The detected board, position, module type and point counts are written to the log.
Each mapped direction must have 16 or 32 points; nonexistent modules, wrong
directions and unsupported point counts stop initialization without writing outputs.

Default input modules are `[0,1,4]`; output modules are `[2,3,4]`. Input scanning
uses WORD offsets 0 and 1 for 32 DI, but only offset 0 for 16 DI. WORD offsets are
word indices, not bit offsets. The two words are combined into the existing uint
slot, with the upper 16 bits cleared for a 16-point module. Existing logical
addresses remain unchanged: each configured module still occupies a 32-bit slot.
Access to bits 16..31 of a 16-point module is rejected before calling native I/O.

The controller captures its module lists and interrupt number when constructed.
Editing settings cannot redirect a running input scan;
restart is required. Closing and reinitializing refreshes the hardware point counts.
The shared physical input monitor still faults if either provider fails; no
communication error is converted to an OFF input or successful machine readiness.

## Motion implementation

`AjinMotionService` uses `C:/git/AnyWave/AnyWave.Device/Motions/Ajin/AjinService.cs`
as its reference. IBTM adds configured axis mappings, HOME direction and stage speeds,
acceleration times, shared initialization, cancellation and completion feedback.
The AnyWave static initializer that closes AXL and loads `Settings/Default.mot`
is not called by each IBTM motion group; `AjinController` owns that shared connection.

- HOME reads each axis's SDK method and replaces its direction with `AxisHardware.HomeDirection`.
  The SDK signal, Z-phase, clear time and offset are retained for all axes, including Z.
- Before HOME starts, the code sets actual position to zero, sets `HOME_ERR_UNKNOWN`,
  and applies the method and velocities.
- Search velocity comes from the caller; the other stage velocities and acceleration
  times come from that axis group's `HomeSettings`. Acceleration is stage speed divided by its configured time.
- HOME polls at 100 ms and returns false for a failed startup or result.
- Position moves use velocity divided by `AccelerationSeconds` / `DecelerationSeconds`.
  The default 0.5-second settings produce velocity × 2.
  XY speed is distributed by `distanceX / (distanceX + distanceY)` and the Y equivalent.
- Native command return values and position/stop feedback are checked by IBTM.
  Cancellation stops the commanded axes and waits for feedback before releasing the operation.
  There is no additional STOP callback that duplicates cancellation cleanup.

HOME stage speeds, acceleration times and axis direction are editable in Settings.
The driver retains its mm conversion and configured SDK move units.

`AxlOpen` initializes the library and hardware; it does not promise to preserve
previous hardware settings. `AjinMotionService` applies configured move units and
acceleration units in pulses/s².
Initialization does not send Servo ON or an explicit axis-alarm reset. Once initialized,
position and signal feedback remain readable with servos OFF or axis alarms active.
Read-only diagnostic getters also work for motion groups that have not been initialized,
using the already-open AXL connection without changing parameters or servo state.
`MachineFeedbackMonitor` owns the input, output and motion monitoring lifetimes.
`PhysicalIoService.RefreshInputs` performs one complete two-provider scan; it does not
start a background task. Recovery publishes input changes against the last complete
snapshot; a failed recovery read cannot partially overwrite that snapshot. Initial
startup levels are not reported as new input edges. Input polling remains at 10 ms; outputs and motion use 250 ms
polling with device notifications requesting an earlier read. Individual motion query
failures are reported without hiding another axis's feedback. The monitors remain alive
through operation cleanup and stop together before hardware disposal.
Explicit Servo ON failures do not invalidate communication readiness. The existing
operator RESET sequence pulses the axis alarm-reset outputs ON for one second,
then releases them OFF before requesting Servo ON. Cancellation or a command failure
still attempts OFF for every configured axis and reports any release failures.
RESET does not write position or HOME-success values. The machine reads current
alarm/emergency and Servo ON feedback before clearing its software alarm.

Motion STOP checks every `AxmMoveSStop` return code and continues to the remaining
axes after a failure. HOME results are initialized only at startup, not on STOP.
Command/read errors remain attached to stop and stop-feedback errors.
`MotionFailureSurvivesStopAndFeedbackFailures` covers this with the SDK stand-in.

`IBTM.Ajin.Tests` compiles the real controller, motion wrapper and manufacturer constants against
a test-only in-memory SDK stand-in. It never loads AXL.dll or operates equipment.
