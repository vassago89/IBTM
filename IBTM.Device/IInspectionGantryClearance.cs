using System;

namespace IBTM.Device;

public interface IInspectionGantryClearance
{
    event Action? Changed;

    bool IsClear { get; }
}
