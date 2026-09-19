namespace IBTM.BoltFastening;

public sealed class BoltFasteningRecipe
{
    public ushort PcbPreset { get; set; } = 1;
    public ushort IpmSeatingPreset { get; set; } = 1;
    public ushort IpmFinalPreset { get; set; } = 2;
}
