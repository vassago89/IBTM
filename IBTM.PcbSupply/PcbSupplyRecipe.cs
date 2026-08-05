using IBTM.Core;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyRecipe
{
    public XzPos Pcb1PickPosition { get; set; } = new() { X = 4.0, Z = 8.0 };
    public XzPos Pcb2PickPosition { get; set; } = new() { X = 16.0, Z = 8.0 };
}
