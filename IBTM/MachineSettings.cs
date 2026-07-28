using System.Text.Json.Serialization;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;

namespace IBTM;

[JsonConverter(typeof(JsonStringEnumConverter<HardwareDriver>))]
public enum HardwareDriver
{
    Virtual,
    Ajin,
}

public sealed class MachineSettings
{
    public HardwareDriver Driver { get; set; } = HardwareDriver.Virtual;
    public CameraDriver CameraDriver { get; set; } = CameraDriver.Virtual;
    public HardwareMap Hardware { get; set; } = new();
    public MachineOptions Options { get; set; } = new();
    public AjinSettings Ajin { get; set; } = new();
    public HomeSettings Home { get; set; } = new();
    public CameraSettings AlignmentCamera { get; set; } = new();
    public CameraSettings InspectionCamera { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();
    public ConveyorSettings Conveyor { get; set; } = new();
    public PcbSupplySettings PcbSupply { get; set; } = new();
    public PcbPlacementSettings PcbPlacement { get; set; } = new();
    public BoltFasteningSettings BoltFastening { get; set; } = new();
    public InspectionSettings Inspection { get; set; } = new();
}

public sealed class HomeSettings
{
    public double HorizontalSpeed { get; set; } = 15.0;
    public double SpeedZ { get; set; } = 10.0;
}

