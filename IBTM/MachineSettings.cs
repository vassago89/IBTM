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
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM;

public sealed class MachineSettings
{
    public MachineSettings() : this(null)
    {
    }

    private MachineSettings(SavedSettings? values)
    {
        Drivers = values?.Get<DriverSettings>() ?? new();
        Units = values?.Get<UnitSettings>() ?? new();
        Options = values?.Get<MachineOptions>() ?? new();
        RecipeSelection = values?.Get<RecipeSelectionSettings>() ?? new();
        PcbHistory = values?.Get<PcbHistorySettings>() ?? new();
        Logging = values?.Get<LogSettings>() ?? new();
        CarrierReference = values?.Get<CarrierReferenceSettings>() ?? new();
        Ajin = values?.Get<AjinSettings>() ?? new();
        AlphaMotion = values?.Get<AlphaMotionSettings>() ?? new();
        Hantas = values?.Get<HantasSettings>() ?? new();
        InspectionCamera = values?.Get<InspectionCameraSettings>() ?? new();
        Lighting = values?.Get<LightingSettings>() ?? new();
        NgCarrierTransfer = values?.Get<NgCarrierTransferSettings>() ?? new();
        NgConveyor = values?.Get<NgConveyorSettings>() ?? new();
        Conveyor = values?.Get<ConveyorSettings>() ?? new();
        PcbSupply = values?.Get<PcbSupplySettings>() ?? new();
        PcbSupplyHardware = values?.Get<PcbSupplyHardwareSettings>() ?? new();
        PcbPlacementHandler = values?.Get<PcbPlacementHandlerSettings>() ?? new();
        PcbPlacementHandlerHardware = values?.Get<PcbPlacementHandlerHardwareSettings>() ?? new();
        PcbPlacementStationHardware = values?.Get<PcbPlacementStationHardwareSettings>() ?? new();
        BoltFeeder = values?.Get<BoltFeederSettings>() ?? new();
        BoltFeederHardware = values?.Get<BoltFeederHardwareSettings>() ?? new();
        BoltFastening = values?.Get<BoltFasteningSettings>() ?? new();
        BoltFasteningHardware = values?.Get<BoltFasteningHardwareSettings>() ?? new();
        IoBoltHardware = values?.Get<IoBoltHardwareSettings>() ?? new();
        BoltFasteningStationHardware = values?.Get<BoltFasteningStationHardwareSettings>() ?? new();
        InspectionGantry = values?.Get<InspectionGantrySettings>() ?? new();
        InspectionGantryHardware = values?.Get<InspectionGantryHardwareSettings>() ?? new();
        InspectionStationHardware = values?.Get<InspectionStationHardwareSettings>() ?? new();
        MachineHardware = values?.Get<MachineHardwareSettings>() ?? new();
        ConveyorHardware = values?.Get<ConveyorHardwareSettings>() ?? new();
        NgCarrierTransferHardware = values?.Get<NgCarrierTransferHardwareSettings>() ?? new();
        NgShuttleHardware = values?.Get<NgShuttleHardwareSettings>() ?? new();
        NgConveyorHardware = values?.Get<NgConveyorHardwareSettings>() ?? new();

        // Older settings used one taught position for both waiting and carrier pickup.
        if (values is not null && NgCarrierTransfer.WaitingPosition is null
            && NgCarrierTransfer.CarrierPickupPosition is { } pickup)
            NgCarrierTransfer.WaitingPosition = new() { X = pickup.X, Y = pickup.Y };
    }

    public DriverSettings Drivers { get; set; }
    public UnitSettings Units { get; set; }
    public MachineOptions Options { get; set; }
    public RecipeSelectionSettings RecipeSelection { get; set; }
    public PcbHistorySettings PcbHistory { get; set; }
    public LogSettings Logging { get; set; }
    public CarrierReferenceSettings CarrierReference { get; set; }
    public AjinSettings Ajin { get; set; }
    public AlphaMotionSettings AlphaMotion { get; set; }
    public HantasSettings Hantas { get; set; }
    public InspectionCameraSettings InspectionCamera { get; set; }
    public LightingSettings Lighting { get; set; }
    public NgCarrierTransferSettings NgCarrierTransfer { get; set; }
    public NgConveyorSettings NgConveyor { get; set; }
    public ConveyorSettings Conveyor { get; set; }

    public PcbSupplySettings PcbSupply { get; set; }
    public PcbSupplyHardwareSettings PcbSupplyHardware { get; set; }
    public PcbPlacementHandlerSettings PcbPlacementHandler { get; set; }
    public PcbPlacementHandlerHardwareSettings PcbPlacementHandlerHardware { get; set; }
    public PcbPlacementStationHardwareSettings PcbPlacementStationHardware { get; set; }
    public BoltFeederSettings BoltFeeder { get; set; }
    public BoltFeederHardwareSettings BoltFeederHardware { get; set; }
    public BoltFasteningSettings BoltFastening { get; set; }
    public BoltFasteningHardwareSettings BoltFasteningHardware { get; set; }
    public IoBoltHardwareSettings IoBoltHardware { get; set; }
    public BoltFasteningStationHardwareSettings BoltFasteningStationHardware { get; set; }
    public InspectionGantrySettings InspectionGantry { get; set; }
    public InspectionGantryHardwareSettings InspectionGantryHardware { get; set; }
    public InspectionStationHardwareSettings InspectionStationHardware { get; set; }

    public MachineHardwareSettings MachineHardware { get; set; }
    public ConveyorHardwareSettings ConveyorHardware { get; set; }
    public NgCarrierTransferHardwareSettings NgCarrierTransferHardware { get; set; }
    public NgShuttleHardwareSettings NgShuttleHardware { get; set; }
    public NgConveyorHardwareSettings NgConveyorHardware { get; set; }

    internal (MotionSettings Settings, MotionHardwareSettings Hardware)[] MotionSections
    {
        get
        {
            return [
                (PcbSupply.Motion, PcbSupplyHardware),
                (PcbPlacementHandler.Motion, PcbPlacementHandlerHardware),
                (BoltFastening.Motion, BoltFasteningHardware),
                (InspectionGantry.Motion, InspectionGantryHardware),
            ];
        }
    }

    internal HardwareSettings[] HardwareSections
    {
        get
        {
            return [
                MachineHardware,
                PcbSupplyHardware,
                PcbPlacementHandlerHardware,
                PcbPlacementStationHardware,
                BoltFeederHardware,
                BoltFasteningHardware,
                IoBoltHardware,
                BoltFasteningStationHardware,
                InspectionGantryHardware,
                InspectionStationHardware,
                ConveyorHardware,
                NgCarrierTransferHardware,
                NgShuttleHardware,
                NgConveyorHardware,
            ];
        }
    }

    internal Setting[] Sections
    {
        get
        {
            return [
                .. HardwareSections,
                Drivers,
                Units,
                Options,
                RecipeSelection,
                PcbHistory,
                Logging,
                CarrierReference,
                Ajin,
                AlphaMotion,
                InspectionCamera,
                Lighting,
                Hantas,
                NgCarrierTransfer,
                NgConveyor,
                Conveyor,
                PcbSupply,
                PcbPlacementHandler,
                BoltFeeder,
                BoltFastening,
                InspectionGantry,
            ];
        }
    }

    public static Task<MachineSettings> LoadAsync(
        MachineStore store,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => new MachineSettings(store.LoadSettings()), cancellationToken);
    }
}
