using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<InputIo>))]
public enum InputIo
{
    [Description("Conveyor Upstream Board Available")]
    ConveyorUpstreamBoardAvailable = 1,

    [Description("PCB Buffer PCB Present")]
    PcbBufferPcbPresent = 10,

    [Description("PCB Placement Stopper Up")]
    PcbPlacementStopperUp = 11,

    [Description("PCB Placement Housing 1 Present")]
    PcbPlacementHousing1Present = 12,

    [Description("PCB Placement Backup Plate Up")]
    PcbPlacementBackupPlateUp = 13,

    [Description("Placement Handler Gripper Closed")]
    PcbPlacementGripperClosed = 14,

    [Description("PCB Placement Housing 2 Present")]
    PcbPlacementHousing2Present = 15,

    [Description("PCB Supply SMEMA Board Available")]
    PcbSupplyUpstreamBoardAvailable = 16,

    [Description("Supply Handler Unrotated")]
    PcbSupplyUnrotated = 17,

    [Description("Supply Handler Rotated")]
    PcbSupplyRotated = 18,

    [Description("Supply Handler Gripper Closed")]
    PcbSupplyGripperClosed = 19,

    [Description("Bolt Fastening Carrier Jig Present")]
    BoltFasteningCarrierJigPresent = 20,

    [Description("Bolt Fastening Stopper Up")]
    BoltFasteningStopperUp = 21,

    [Description("Bolt Fastening Housing 1 Present")]
    BoltFasteningHousing1Present = 22,

    [Description("Bolt Fastening Backup Plate Up")]
    BoltFasteningBackupPlateUp = 23,

    [Description("Bolt Fastening Housing 2 Present")]
    BoltFasteningHousing2Present = 24,

    [Description("Bolt Fastening Head 2 Vacuum Detected")]
    BoltFasteningLoctiteVacuumDetected = 25,

    [Description("Inspection Carrier Jig Present")]
    InspectionCarrierJigPresent = 30,

    [Description("Inspection Stopper Up")]
    InspectionStopperUp = 31,

    [Description("Inspection Housing 1 Present")]
    InspectionHousing1Present = 32,

    [Description("Inspection Backup Plate Up")]
    InspectionBackupPlateUp = 33,

    [Description("Inspection Gripper Closed")]
    InspectionGripperClosed = 34,

    [Description("Inspection Housing 2 Present")]
    InspectionHousing2Present = 35,

    [Description("Conveyor Downstream Machine Ready")]
    ConveyorDownstreamMachineReady = 40,

    [Description("Emergency Stop Released")]
    EmergencyStopReleased = 42,

    [Description("Reset Button")]
    ResetButton = 43,

    [Description("Door Closed")]
    DoorClosed = 44,

    [Description("Air Pressure OK")]
    AirPressureOk = 45,

    [Description("Placement Handler PCB Present")]
    PcbPlacementPcbPresent = 46,

    [Description("Supply Handler PCB Present")]
    PcbSupplyPcbPresent = 47,
}

[JsonConverter(typeof(JsonStringEnumConverter<OutputIo>))]
public enum OutputIo
{
    [Description("Conveyor Upstream Machine Ready")]
    ConveyorUpstreamMachineReady = 2,

    [Description("PCB Placement Stopper Up")]
    PcbPlacementStopperUp = 11,

    [Description("PCB Placement Backup Plate Up")]
    PcbPlacementBackupPlateUp = 13,

    [Description("Placement Handler Gripper Close")]
    PcbPlacementGripperClose = 14,

    [Description("Placement Handler Laser")]
    PcbPlacementLaser = 15,

    [Description("PCB Supply SMEMA Machine Ready")]
    PcbSupplyUpstreamMachineReady = 17,

    [Description("Supply Handler Rotate")]
    PcbSupplyRotate = 18,

    [Description("Supply Handler Gripper Close")]
    PcbSupplyGripperClose = 19,

    [Description("Bolt Fastening Stopper Up")]
    BoltFasteningStopperUp = 21,

    [Description("Bolt Fastening Backup Plate Up")]
    BoltFasteningBackupPlateUp = 23,

    [Description("Bolt Fastening Head 2 Vacuum Pump")]
    BoltFasteningLoctiteVacuumPump = 25,

    [Description("Inspection Stopper Up")]
    InspectionStopperUp = 31,

    [Description("Inspection Backup Plate Up")]
    InspectionBackupPlateUp = 33,

    [Description("Inspection Gripper Close")]
    InspectionGripperClose = 34,

    [Description("Inspection Laser")]
    InspectionLaser = 35,

    [Description("Conveyor Downstream Board Available")]
    ConveyorDownstreamBoardAvailable = 41,

    [Description("Tower Lamp Green")]
    TowerLampGreen = 42,

    [Description("Tower Lamp Yellow")]
    TowerLampYellow = 43,

    [Description("Tower Lamp Red")]
    TowerLampRed = 44,

    [Description("Buzzer")]
    Buzzer = 45,
}

[JsonConverter(typeof(JsonStringEnumConverter<MachineAxis>))]
public enum MachineAxis
{
    [Description("PCB Pickup Transfer X")]
    PcbSupplyX,

    [Description("PCB Pickup Transfer Z")]
    PcbSupplyZ,

    [Description("PCB Handler & Place X")]
    PcbPlacementX,

    [Description("PCB Handler & Place Y")]
    PcbPlacementY,

    [Description("PCB Handler & Place Z")]
    PcbPlacementZ,

    [Description("Bolt Fastening X")]
    BoltFasteningX,

    [Description("Bolt Fastening Y")]
    BoltFasteningY,

    [Description("Bolt Fastening Z")]
    BoltFasteningZ,

    [Description("NG Transfer X")]
    InspectionX,

    [Description("NG Transfer Y")]
    InspectionY,

    [Description("NG Transfer Z")]
    InspectionZ,

    [Description("Conveyor")]
    Conveyor,
}

[JsonConverter(typeof(JsonStringEnumConverter<AxisDirection>))]
public enum AxisDirection
{
    [Description("Positive")]
    Positive = 1,

    [Description("Negative")]
    Negative = -1,
}

public sealed class OutputFeedback
{
    public OutputFeedback()
    {
    }

    public OutputFeedback(InputIo input)
    {
        OnInput = input;
        OffInput = input;
    }

    public OutputFeedback(InputIo onInput, InputIo offInput)
    {
        OnInput = onInput;
        OffInput = offInput;
        OffValue = true;
    }

    public InputIo OnInput { get; set; }
    public bool OnValue { get; set; } = true;
    public InputIo OffInput { get; set; }
    public bool OffValue { get; set; }
    public int TimeoutMilliseconds { get; set; } = 3_000;

    public (InputIo Input, bool Value) GetExpected(bool outputValue) =>
        outputValue
            ? (OnInput, OnValue)
            : (OffInput, OffValue);
}

public sealed class HardwareMap : Setting
{
    public double MillimetersPerPulse { get; set; } = 0.01;

    public Dictionary<InputIo, int> Inputs { get; set; } =
        Enum.GetValues<InputIo>().ToDictionary(io => io, io => (int)io);

    public Dictionary<OutputIo, int> Outputs { get; set; } =
        Enum.GetValues<OutputIo>().ToDictionary(io => io, io => (int)io);

    public Dictionary<MachineAxis, int> Axes { get; set; } =
        Enum.GetValues<MachineAxis>().ToDictionary(axis => axis, axis => (int)axis);

    public Dictionary<MachineAxis, AxisDirection> AxisDirections { get; set; } =
        Enum.GetValues<MachineAxis>().ToDictionary(
            axis => axis,
            _ => AxisDirection.Positive);

    public Dictionary<MachineAxis, double> AxisMinimums { get; set; } =
        Enum.GetValues<MachineAxis>().ToDictionary(axis => axis, _ => 0.0);

    public Dictionary<MachineAxis, double> AxisMaximums { get; set; } =
        Enum.GetValues<MachineAxis>().ToDictionary(
            axis => axis,
            axis => axis switch
            {
                MachineAxis.PcbSupplyX => 200.0,
                MachineAxis.PcbSupplyZ => 100.0,
                MachineAxis.PcbPlacementX => 200.0,
                MachineAxis.PcbPlacementY => 400.0,
                MachineAxis.PcbPlacementZ => 200.0,
                MachineAxis.BoltFasteningX => 200.0,
                MachineAxis.BoltFasteningY => 200.0,
                MachineAxis.BoltFasteningZ => 200.0,
                MachineAxis.InspectionX => 200.0,
                MachineAxis.InspectionY => 200.0,
                MachineAxis.InspectionZ => 200.0,
                _ => 0.0,
            });

    public Dictionary<OutputIo, OutputFeedback> OutputFeedbacks { get; set; } =
        new()
        {
            [OutputIo.PcbSupplyRotate] = new(
                InputIo.PcbSupplyRotated,
                InputIo.PcbSupplyUnrotated),
            [OutputIo.PcbSupplyGripperClose] = new(InputIo.PcbSupplyGripperClosed),
            [OutputIo.PcbPlacementStopperUp] = new(InputIo.PcbPlacementStopperUp),
            [OutputIo.PcbPlacementBackupPlateUp] = new(
                InputIo.PcbPlacementBackupPlateUp),
            [OutputIo.PcbPlacementGripperClose] = new(
                InputIo.PcbPlacementGripperClosed),
            [OutputIo.BoltFasteningStopperUp] = new(
                InputIo.BoltFasteningStopperUp),
            [OutputIo.BoltFasteningBackupPlateUp] = new(
                InputIo.BoltFasteningBackupPlateUp),
            [OutputIo.InspectionStopperUp] = new(InputIo.InspectionStopperUp),
            [OutputIo.InspectionBackupPlateUp] = new(
                InputIo.InspectionBackupPlateUp),
            [OutputIo.InspectionGripperClose] = new(
                InputIo.InspectionGripperClosed),
        };

}
