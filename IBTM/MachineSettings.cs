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
using IBTM.Storage;

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

    public Task SaveAsync(MachineStore store, CancellationToken cancellationToken = default) =>
        Task.Run(() => store.SaveSettings(Sections, cancellationToken), cancellationToken);

    internal Setting[] Sections =>
    [
        .. HardwareSections,
        Drivers, Units, Options, Home, RecipeSelection, CarrierReference,
        Ajin, AlphaMotion, InspectionCamera, Lighting, Hantas,
        NgCarrierTransfer, NgConveyor, PcbBuffer, PcbSupply,
        PcbPlacementHandler, BoltFeeder, BoltFastening, InspectionGantry,
    ];

    public static Task<MachineSettings> LoadAsync(MachineStore store,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => From(store.LoadSettings()), cancellationToken);

    internal static MachineSettings From(SavedSettings values) => new()
    {
        Drivers = values.Get<DriverSettings>(),
        Units = values.Get<UnitSettings>(),
        Options = values.Get<MachineOptions>(),
        Home = values.Get<HomeSettings>(),
        RecipeSelection = values.Get<RecipeSelectionSettings>(),
        CarrierReference = values.Get<CarrierReferenceSettings>(),
        MachineHardware = values.Get<MachineHardwareSettings>(),
        ConveyorHardware = values.Get<ConveyorHardwareSettings>(),
        Ajin = values.Get<AjinSettings>(),
        AlphaMotion = values.Get<AlphaMotionSettings>(),
        InspectionCamera = values.Get<InspectionCameraSettings>(),
        Lighting = values.Get<LightingSettings>(),
        Hantas = values.Get<HantasSettings>(),
        NgCarrierTransfer = values.Get<NgCarrierTransferSettings>(),
        NgConveyor = values.Get<NgConveyorSettings>(),
        PcbBuffer = values.Get<PcbBufferSettings>(),
        PcbBufferHardware = values.Get<PcbBufferHardwareSettings>(),
        PcbSupply = values.Get<PcbSupplySettings>(),
        PcbSupplyHardware = values.Get<PcbSupplyHardwareSettings>(),
        PcbPlacementHandler = values.Get<PcbPlacementHandlerSettings>(),
        PcbPlacementHandlerHardware = values.Get<PcbPlacementHandlerHardwareSettings>(),
        PcbPlacementStationHardware = values.Get<PcbPlacementStationHardwareSettings>(),
        BoltFeeder = values.Get<BoltFeederSettings>(),
        BoltFeederHardware = values.Get<BoltFeederHardwareSettings>(),
        BoltFastening = values.Get<BoltFasteningSettings>(),
        BoltFasteningHardware = values.Get<BoltFasteningHardwareSettings>(),
        BoltFasteningStationHardware = values.Get<BoltFasteningStationHardwareSettings>(),
        InspectionGantry = values.Get<InspectionGantrySettings>(),
        InspectionStationHardware = values.Get<InspectionStationHardwareSettings>(),
        InspectionGantryHardware = values.Get<InspectionGantryHardwareSettings>(),
        NgCarrierTransferHardware = values.Get<NgCarrierTransferHardwareSettings>(),
        NgShuttleHardware = values.Get<NgShuttleHardwareSettings>(),
        NgConveyorHardware = values.Get<NgConveyorHardwareSettings>(),
    };
}

