using IBTM.Core;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyRecipe
{
    public XzPos CarrierPick1 { get; set; } = new() { X = 75.0, Z = 25.0 };
    public XzPos CarrierPick2 { get; set; } = new() { X = 130.0, Z = 25.0 };
}
