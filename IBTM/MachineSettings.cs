using System.ComponentModel;
using System.Text.Json.Serialization;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;

namespace IBTM;

[JsonConverter(typeof(JsonStringEnumConverter<ControlDriver>))]
public enum ControlDriver
{
    [Description("Virtual")]
    Virtual,

    [Description("AJIN")]
    Ajin,
}

public sealed class MachineSettings : Setting
{
    public ControlDriver ControlDriver { get; set; } = ControlDriver.Virtual;
    public CameraDriver CameraDriver { get; set; } = CameraDriver.Virtual;
    [JsonIgnore]
    public HardwareMap Hardware { get; set; } = new();
    public MachineOptions Options { get; set; } = new();
    [JsonIgnore]
    public AjinSettings Ajin { get; set; } = new();
    public HomeSettings Home { get; set; } = new();
    [JsonIgnore]
    public AlignmentCameraSettings AlignmentCamera { get; set; } = new();
    [JsonIgnore]
    public InspectionCameraSettings InspectionCamera { get; set; } = new();
    [JsonIgnore]
    public LightingSettings Lighting { get; set; } = new();
    public double ConveyorVelocity { get; set; } = 100.0;
    [JsonIgnore]
    public PcbBufferSettings PcbBuffer { get; set; } = new();
    [JsonIgnore]
    public PcbSupplySettings PcbSupply { get; set; } = new();
    [JsonIgnore]
    public PcbPlacementSettings PcbPlacement { get; set; } = new();
    [JsonIgnore]
    public BoltFasteningSettings BoltFastening { get; set; } = new();
    [JsonIgnore]
    public InspectionSettings Inspection { get; set; } = new();
}

public sealed class HomeSettings
{
    public double HorizontalSpeed { get; set; } = 15.0;
    public double ZSpeed { get; set; } = 10.0;
}

