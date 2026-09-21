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
    public MachineSettings()
    {
        Drivers = new();
        Units = new();
        Options = new();
        RecipeSelection = new();
        PcbHistory = new();
        CarrierReference = new();
        Ajin = new();
        AlphaMotion = new();
        Hantas = new();
        InspectionCamera = new();
        Lighting = new();
        NgCarrierTransfer = new();
        NgConveyor = new();
        Conveyor = new();
        PcbSupply = new();
        PcbSupplyHardware = new();
        PcbPlacementHandler = new();
        PcbPlacementHandlerHardware = new();
        PcbPlacementStationHardware = new();
        BoltFeeder = new();
        BoltFeederHardware = new();
        BoltFastening = new();
        BoltFasteningHardware = new();
        IoBoltHardware = new();
        BoltFasteningStationHardware = new();
        InspectionGantry = new();
        InspectionGantryHardware = new();
        InspectionStationHardware = new();
        MachineHardware = new();
        ConveyorHardware = new();
        NgCarrierTransferHardware = new();
        NgShuttleHardware = new();
        NgConveyorHardware = new();
    }

    public DriverSettings Drivers { get; set; }
    public UnitSettings Units { get; set; }
    public MachineOptions Options { get; set; }
    public RecipeSelectionSettings RecipeSelection { get; set; }
    public PcbHistorySettings PcbHistory { get; set; }
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

    public async Task SaveAsync(MachineStore store, CancellationToken cancellationToken = default)
    {
        await store.SaveSettingsAsync(Sections, cancellationToken);
    }

    public static Task<MachineSettings> LoadAsync(
        MachineStore store,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => From(store.LoadSettings()), cancellationToken);
    }

    internal static MachineSettings From(SavedSettings values)
    {
        return new()
        {
            Drivers = values.Get<DriverSettings>(),
            Units = values.Get<UnitSettings>(),
            Options = values.Get<MachineOptions>(),
            RecipeSelection = values.Get<RecipeSelectionSettings>(),
            PcbHistory = values.Get<PcbHistorySettings>(),
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
            Conveyor = values.Get<ConveyorSettings>(),
            PcbSupply = values.Get<PcbSupplySettings>(),
            PcbSupplyHardware = values.Get<PcbSupplyHardwareSettings>(),
            PcbPlacementHandler = values.Get<PcbPlacementHandlerSettings>(),
            PcbPlacementHandlerHardware = values.Get<PcbPlacementHandlerHardwareSettings>(),
            PcbPlacementStationHardware = values.Get<PcbPlacementStationHardwareSettings>(),
            BoltFeeder = values.Get<BoltFeederSettings>(),
            BoltFeederHardware = values.Get<BoltFeederHardwareSettings>(),
            BoltFastening = values.Get<BoltFasteningSettings>(),
            BoltFasteningHardware = values.Get<BoltFasteningHardwareSettings>(),
            IoBoltHardware = values.Get<IoBoltHardwareSettings>(),
            BoltFasteningStationHardware = values.Get<BoltFasteningStationHardwareSettings>(),
            InspectionGantry = values.Get<InspectionGantrySettings>(),
            InspectionStationHardware = values.Get<InspectionStationHardwareSettings>(),
            InspectionGantryHardware = values.Get<InspectionGantryHardwareSettings>(),
            NgCarrierTransferHardware = values.Get<NgCarrierTransferHardwareSettings>(),
            NgShuttleHardware = values.Get<NgShuttleHardwareSettings>(),
            NgConveyorHardware = values.Get<NgConveyorHardwareSettings>(),
        };
    }
}
