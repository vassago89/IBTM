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

public sealed class DriverSettings : Setting
{
    public ControlDriver Control { get; set; } = ControlDriver.Virtual;
    public CameraDriver Camera { get; set; } = CameraDriver.Virtual;
    public BoltDriver Bolt
    {
        get;
        set => field = value == BoltDriver.Io ? BoltDriver.HantasAdc : value;
    } = BoltDriver.Virtual;
    public LightDriver Light { get; set; } = LightDriver.Virtual;
}
