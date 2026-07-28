using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<InputIo>))]
public enum InputIo
{
    MainLaneUpstreamBoardAvailable = 1,

    PcbPlacementCarrierJigPresent = 10,
    PcbPlacementStopperUp = 11,
    PcbPlacementHousing1Present = 12,
    PcbPlacementBackupPlateUp = 13,
    PcbPlacementGripperClosed = 14,
    PcbPlacementHousing2Present = 15,

    PcbSupplyCarrierAvailable = 16,
    PcbSupplyRotationHome = 17,
    PcbSupplyRotationHandoff = 18,
    PcbSupplyGripperClosed = 19,

    BoltFasteningCarrierJigPresent = 20,
    BoltFasteningStopperUp = 21,
    BoltFasteningHousing1Present = 22,
    BoltFasteningBackupPlateUp = 23,
    BoltFasteningHousing2Present = 24,

    InspectionCarrierJigPresent = 30,
    InspectionStopperUp = 31,
    InspectionHousing1Present = 32,
    InspectionBackupPlateUp = 33,
    InspectionGripper = 34,
    InspectionHousing2Present = 35,

    MainLaneDownstreamMachineReady = 40,
    EmergencyStopReleased = 42,
    ResetButton = 43,
    DoorClosed = 44,
    AirPressureOk = 45,
    PcbPlacementPcbPresent = 46,
    PcbSupplyPcbPresent = 47,
}

[JsonConverter(typeof(JsonStringEnumConverter<OutputIo>))]
public enum OutputIo
{
    MainLaneUpstreamMachineReady = 2,

    PcbPlacementStopperUp = 11,
    PcbPlacementBackupPlateUp = 13,
    PcbPlacementGripper = 14,
    PcbPlacementLaser = 15,

    PcbSupplyReady = 17,
    PcbSupplyRotateToHandoff = 18,
    PcbSupplyGripper = 19,

    BoltFasteningStopperUp = 21,
    BoltFasteningBackupPlateUp = 23,
    BoltFasteningLaser = 25,

    InspectionStopperUp = 31,
    InspectionBackupPlateUp = 33,
    InspectionGripper = 34,
    InspectionLaser = 35,

    MainLaneDownstreamBoardAvailable = 41,
    TowerLampGreen = 42,
    TowerLampYellow = 43,
    TowerLampRed = 44,
    Buzzer = 45,
}

[JsonConverter(typeof(JsonStringEnumConverter<MachineAxis>))]
public enum MachineAxis
{
    PcbSupplyX,
    PcbSupplyZ,
    PcbPlacementX,
    PcbPlacementY,
    PcbPlacementZ,
    BoltFasteningX,
    BoltFasteningY,
    BoltFasteningZ,
    InspectionX,
    InspectionY,
    InspectionZ,
    Conveyor,
}

[JsonConverter(typeof(JsonStringEnumConverter<AxisDirection>))]
public enum AxisDirection
{
    Positive = 1,
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

public sealed class HardwareMap
{
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

    public Dictionary<OutputIo, OutputFeedback> OutputFeedbacks { get; set; } =
        new()
        {
            [OutputIo.PcbSupplyRotateToHandoff] = new(
                InputIo.PcbSupplyRotationHandoff,
                InputIo.PcbSupplyRotationHome),
            [OutputIo.PcbSupplyGripper] = new(InputIo.PcbSupplyGripperClosed),
            [OutputIo.PcbPlacementStopperUp] = new(InputIo.PcbPlacementStopperUp),
            [OutputIo.PcbPlacementBackupPlateUp] = new(
                InputIo.PcbPlacementBackupPlateUp),
            [OutputIo.PcbPlacementGripper] = new(
                InputIo.PcbPlacementGripperClosed),
            [OutputIo.BoltFasteningStopperUp] = new(
                InputIo.BoltFasteningStopperUp),
            [OutputIo.BoltFasteningBackupPlateUp] = new(
                InputIo.BoltFasteningBackupPlateUp),
            [OutputIo.InspectionStopperUp] = new(InputIo.InspectionStopperUp),
            [OutputIo.InspectionBackupPlateUp] = new(
                InputIo.InspectionBackupPlateUp),
            [OutputIo.InspectionGripper] = new(InputIo.InspectionGripper),
        };

}
