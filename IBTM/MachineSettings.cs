using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public sealed class MachineSettings
{
    public DriverSettings Drivers { get; set; } = new();
    public UnitSettings Units { get; set; } = new();
    public MachineOptions Options { get; set; } = new();
    public HomeSettings Home { get; set; } = new();
    public RecipeSelectionSettings RecipeSelection { get; set; } = new();
    public CarrierReferenceSettings CarrierReference { get; set; } = new();
    public AjinSettings Ajin { get; set; } = new();
    public AlphaMotionSettings AlphaMotion { get; set; } = new();
    public HantasSettings Hantas { get; set; } = new();
    public InspectionCameraSettings InspectionCamera { get; set; } = new();
    public BoltInspectionSettings BoltInspection { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();
    public NgCarrierTransferSettings NgCarrierTransfer { get; set; } = new();
    public NgConveyorSettings NgConveyor { get; set; } = new();

    public PcbSupplySettings PcbSupply { get; set; } = new();
    public PcbSupplyHardwareSettings PcbSupplyHardware { get; set; } = new();
    public PcbBufferSettings PcbBuffer { get; set; } = new();
    public PcbBufferHardwareSettings PcbBufferHardware { get; set; } = new();
    public PcbPlacementHandlerSettings PcbPlacementHandler { get; set; } = new();
    public PcbPlacementHandlerHardwareSettings PcbPlacementHandlerHardware { get; set; } = new();
    public PcbPlacementStationHardwareSettings PcbPlacementStationHardware { get; set; } = new();
    public BoltFeederSettings BoltFeeder { get; set; } = new();
    public BoltFeederHardwareSettings BoltFeederHardware { get; set; } = new();
    public BoltFasteningSettings BoltFastening { get; set; } = new();
    public BoltFasteningHardwareSettings BoltFasteningHardware { get; set; } = new();
    public BoltFasteningStationHardwareSettings BoltFasteningStationHardware { get; set; } = new();
    public InspectionGantrySettings InspectionGantry { get; set; } = new();
    public InspectionGantryHardwareSettings InspectionGantryHardware { get; set; } = new();
    public InspectionStationHardwareSettings InspectionStationHardware { get; set; } = new();

    public MachineHardwareSettings MachineHardware { get; set; } = new();
    public ConveyorHardwareSettings ConveyorHardware { get; set; } = new();
    public NgCarrierTransferHardwareSettings NgCarrierTransferHardware { get; set; } = new();
    public NgShuttleHardwareSettings NgShuttleHardware { get; set; } = new();
    public NgConveyorHardwareSettings NgConveyorHardware { get; set; } = new();

    internal (MotionSettings Settings, MotionHardwareSettings Hardware)[] MotionSections =>
    [
        (PcbSupply.Motion, PcbSupplyHardware),
        (PcbPlacementHandler.Motion, PcbPlacementHandlerHardware),
        (BoltFastening.Motion, BoltFasteningHardware),
        (InspectionGantry.Motion, InspectionGantryHardware),
    ];

    internal HardwareSettings[] HardwareSections =>
    [
        MachineHardware,
        PcbSupplyHardware,
        PcbBufferHardware,
        PcbPlacementHandlerHardware,
        PcbPlacementStationHardware,
        BoltFeederHardware,
        BoltFasteningHardware,
        BoltFasteningStationHardware,
        InspectionGantryHardware,
        InspectionStationHardware,
        ConveyorHardware,
        NgCarrierTransferHardware,
        NgShuttleHardware,
        NgConveyorHardware,
    ];

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(Sections.Select(section => section.SaveAsync(cancellationToken)));

    private Setting[] Sections =>
    [
        .. HardwareSections,
        Drivers, Units, Options, Home, RecipeSelection, CarrierReference,
        Ajin, AlphaMotion, InspectionCamera, BoltInspection, Lighting, Hantas,
        NgCarrierTransfer, NgConveyor, PcbBuffer, PcbSupply,
        PcbPlacementHandler, BoltFeeder, BoltFastening, InspectionGantry,
    ];

    public static async Task<MachineSettings> LoadAsync(
        CancellationToken cancellationToken = default) => new()
    {
        Drivers = await Setting.LoadAsync<DriverSettings>(cancellationToken),
        Units = await Setting.LoadAsync<UnitSettings>(cancellationToken),
        Options = await Setting.LoadAsync<MachineOptions>(cancellationToken),
        Home = await Setting.LoadAsync<HomeSettings>(cancellationToken),
        RecipeSelection = await Setting.LoadAsync<RecipeSelectionSettings>(cancellationToken),
        CarrierReference = await Setting.LoadAsync<CarrierReferenceSettings>(cancellationToken),
        MachineHardware = await Setting.LoadAsync<MachineHardwareSettings>(cancellationToken),
        ConveyorHardware = await Setting.LoadAsync<ConveyorHardwareSettings>(cancellationToken),
        Ajin = await Setting.LoadAsync<AjinSettings>(cancellationToken),
        AlphaMotion = await Setting.LoadAsync<AlphaMotionSettings>(cancellationToken),
        InspectionCamera = await Setting.LoadAsync<InspectionCameraSettings>(cancellationToken),
        BoltInspection = await Setting.LoadAsync<BoltInspectionSettings>(cancellationToken),
        Lighting = await Setting.LoadAsync<LightingSettings>(cancellationToken),
        Hantas = await Setting.LoadAsync<HantasSettings>(cancellationToken),
        NgCarrierTransfer = await Setting.LoadAsync<NgCarrierTransferSettings>(cancellationToken),
        NgConveyor = await Setting.LoadAsync<NgConveyorSettings>(cancellationToken),
        PcbBuffer = await Setting.LoadAsync<PcbBufferSettings>(cancellationToken),
        PcbBufferHardware = await Setting.LoadAsync<PcbBufferHardwareSettings>(cancellationToken),
        PcbSupply = await Setting.LoadAsync<PcbSupplySettings>(cancellationToken),
        PcbSupplyHardware = await Setting.LoadAsync<PcbSupplyHardwareSettings>(cancellationToken),
        PcbPlacementHandler = await Setting.LoadAsync<PcbPlacementHandlerSettings>(cancellationToken),
        PcbPlacementHandlerHardware = await Setting.LoadAsync<PcbPlacementHandlerHardwareSettings>(cancellationToken),
        PcbPlacementStationHardware = await Setting.LoadAsync<PcbPlacementStationHardwareSettings>(cancellationToken),
        BoltFeeder = await Setting.LoadAsync<BoltFeederSettings>(cancellationToken),
        BoltFeederHardware = await Setting.LoadAsync<BoltFeederHardwareSettings>(cancellationToken),
        BoltFastening = await Setting.LoadAsync<BoltFasteningSettings>(cancellationToken),
        BoltFasteningHardware = await Setting.LoadAsync<BoltFasteningHardwareSettings>(cancellationToken),
        BoltFasteningStationHardware = await Setting.LoadAsync<BoltFasteningStationHardwareSettings>(cancellationToken),
        InspectionGantry = await Setting.LoadAsync<InspectionGantrySettings>(cancellationToken),
        InspectionStationHardware = await Setting.LoadAsync<InspectionStationHardwareSettings>(cancellationToken),
        InspectionGantryHardware = await Setting.LoadAsync<InspectionGantryHardwareSettings>(cancellationToken),
        NgCarrierTransferHardware = await Setting.LoadAsync<NgCarrierTransferHardwareSettings>(cancellationToken),
        NgShuttleHardware = await Setting.LoadAsync<NgShuttleHardwareSettings>(cancellationToken),
        NgConveyorHardware = await Setting.LoadAsync<NgConveyorHardwareSettings>(cancellationToken),
    };
}

