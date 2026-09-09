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

The corresponding native `AXL.dll` must still be available on the machine.
Copying these declarations does not install or initialize the native driver.
IBTM retains its configured interrupt number, axis scaling and RTEX module mappings.
The saved motion-parameter file path is retained for compatibility but is not loaded
in the current `AxlOpenNoReset` field-test startup.

## DIO initialization and scanning

The manufacturer's `Visual C#/DIO/DigitalIO/FormDigitalIO.cs` sample opens AXL
with `AXT_RT_SUCCESS == 0`, queries the DIO modules, and reads inputs in WORD units.
IBTM now uses the manufacturer's `AxlOpenNoReset` to avoid resetting the hardware
chip and skips `AxmMotLoadParaAll`. There is no automatic fallback to `AxlOpen`;
the open result is logged and a nonzero result fails initialization. It then checks
DIO presence, module count, and each configured module's identity and DI/DO counts.
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

This is an equipment-side field test, not a verified guarantee that axis settings
persist across power cycles. Existing home/signal settings must already be valid.
`AjinMotionService` still sets move units to 1/1 and acceleration units to pulses/s²
and applies home speeds when homing; the driver's mm conversion is unchanged.
Initialization does not send Servo ON or reset axis alarms. Once initialized,
position and signal feedback remain readable with servos OFF or axis alarms active.
Explicit Servo ON failures do not invalidate communication readiness. The existing
operator RESET sequence still resets axis alarms before requesting Servo ON.

`IBTM.Ajin.Tests` compiles the real controller, motion wrapper and manufacturer constants against
a test-only in-memory SDK stand-in. It never loads AXL.dll or operates equipment.
