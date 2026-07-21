namespace IBTM.Infrastructure.Persistence;

internal sealed class LegacyRecipe
{
    public string Name { get; set; } = "Default";
    public AxisPos Zone1_PcbPick1 { get; set; } = new();
    public AxisPos Zone1_PcbPlace1 { get; set; } = new();
    public AxisPos Zone1_PcbPick2 { get; set; } = new();
    public AxisPos Zone1_PcbPlace2 { get; set; } = new();
    public AxisPos Zone2_FiducialPos { get; set; } = new();
    public double Zone2_Pcb1CenterX { get; set; }
    public double Zone2_Pcb2CenterX { get; set; }
    public double Zone2_PcbCenterY { get; set; }
    public List<BoltPoint> BoltPoints { get; set; } = [];
    public AxisPos Zone3_InspectPos { get; set; } = new();
    public AxisPos Zone3_NgPickupPos { get; set; } = new();
    public AxisPos Zone3_NgPlacePos { get; set; } = new();

    public Recipe ToCurrent() => new()
    {
        Name = Name,
        PcbPlacement = new PcbPlacementRecipe
        {
            PcbPick1 = Zone1_PcbPick1,
            PcbPlace1 = Zone1_PcbPlace1,
            PcbPick2 = Zone1_PcbPick2,
            PcbPlace2 = Zone1_PcbPlace2,
        },
        BoltFastening = new BoltFasteningRecipe
        {
            FiducialPosition = Zone2_FiducialPos,
            Pcb1CenterX = Zone2_Pcb1CenterX,
            Pcb2CenterX = Zone2_Pcb2CenterX,
            PcbCenterY = Zone2_PcbCenterY,
            BoltPoints = BoltPoints,
        },
        Inspection = new InspectionRecipe
        {
            InspectPosition = Zone3_InspectPos,
            NgPickupPosition = Zone3_NgPickupPos,
            NgPlacePosition = Zone3_NgPlacePos,
        },
    };
}

internal sealed class LegacyMachineConfig
{
    public AxisPos Zone1Ref { get; set; } = new();
    public AxisPos Zone2Ref { get; set; } = new();
    public AxisPos Zone3Ref { get; set; } = new();
    public AxisPos Offset3To1 { get; set; } = new();
    public AxisPos Offset3To2 { get; set; } = new();
    public ZoneMotionParams Zone1Motion { get; set; } = new();
    public ZoneMotionParams Zone2Motion { get; set; } = new();
    public ZoneMotionParams Zone3Motion { get; set; } = new();
    public double DefaultTorqueNm { get; set; } = 15.0;
    public int BoltRetryCount { get; set; } = 2;
    public double PixelsPerMm { get; set; } = 50.0;
    public int LiftSettleDelayMs { get; set; } = 500;
    public int AlignSettleDelayMs { get; set; } = 300;
    public int NgStackMaxCount { get; set; } = 3;
    public string Language { get; set; } = "en";

    public MachineConfig ToCurrent() => new()
    {
        Calibration = new CalibrationSettings
        {
            Zone1Ref = Zone1Ref,
            Zone2Ref = Zone2Ref,
            Zone3Ref = Zone3Ref,
            Offset3To1 = Offset3To1,
            Offset3To2 = Offset3To2,
        },
        Runtime = new MachineRuntimeSettings
        {
            LiftSettleDelayMs = LiftSettleDelayMs,
            AlignSettleDelayMs = AlignSettleDelayMs,
        },
        PcbPlacement = new PcbPlacementOptions { Motion = Zone1Motion },
        BoltFastening = new BoltFasteningOptions
        {
            Motion = Zone2Motion,
            DefaultTorqueNm = DefaultTorqueNm,
            RetryCount = BoltRetryCount,
            PixelsPerMm = PixelsPerMm,
        },
        Inspection = new InspectionOptions
        {
            Motion = Zone3Motion,
            NgStackMaxCount = NgStackMaxCount,
        },
        System = new SystemSettings
        {
            Language = Language,
        },
    };
}
