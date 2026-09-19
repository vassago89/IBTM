namespace IBTM.PcbSupply;

public sealed class PcbSupplyRecipe
{
    public PcbSupplyRecipe()
    {
        Pcb1PickPosition = new();
        Pcb2PickPosition = new();
    }

    public PcbPickPosition Pcb1PickPosition { get; set; }
    public PcbPickPosition Pcb2PickPosition { get; set; }
}

public sealed class PcbPickPosition
{
    public double X { get; set; }
    public double Z { get; set; }
}
