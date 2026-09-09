using System.ComponentModel;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<ControlDriver>))]
public enum ControlDriver
{
    [Description("Virtual")]
    Virtual,

    [Description("Physical")]
    Physical,
}

[JsonConverter(typeof(JsonStringEnumConverter<InspectionAlgorithm>))]
public enum InspectionAlgorithm
{
    [Description("Simulated")]
    Virtual,

    [Description("Tiny U-Net")]
    TinyUnet,
}

public sealed class DriverSettings : Setting
{
    public ControlDriver Control { get; set; } = ControlDriver.Virtual;
    public CameraDriver Camera { get; set; } = CameraDriver.Virtual;
    public BoltDriver Bolt { get; set; } = BoltDriver.Virtual;
    public LightDriver Light { get; set; } = LightDriver.Virtual;
    public InspectionAlgorithm Inspection { get; set; } = InspectionAlgorithm.TinyUnet;
}
