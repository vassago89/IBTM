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
IBTM retains its configured interrupt number, motion parameter file, axis scaling
and RTEX module mappings.
