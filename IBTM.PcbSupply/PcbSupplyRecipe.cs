namespace IBTM.PcbSupply;

public sealed class PcbSupplyRecipe
{
    public PcbPickPosition Pcb1PickPosition { get; set; } = new();
    public PcbPickPosition Pcb2PickPosition { get; set; } = new();
}

public sealed class PcbPickPosition
{
    public double X { get; set; }
    public double Z { get; set; }
}
