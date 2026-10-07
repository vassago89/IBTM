using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Tests;

internal sealed class TestMotionHardware : MotionHardwareSettings
{
    public TestMotionHardware(AxisHardware axisX, AxisHardware? axisY, AxisHardware? axisZ)
        : base(MotionGroup.PcbSupply,
            new[]
            {
                (Axis: MotionAxis.X, Signal: MachineAxis.PcbSupplyX, Hardware: axisX),
                (Axis: MotionAxis.Y, Signal: MachineAxis.PcbSupplyY, Hardware: axisY),
                (Axis: MotionAxis.Z, Signal: MachineAxis.PcbSupplyZ, Hardware: axisZ),
            }
            .Where(axis => axis.Hardware is not null)
            .Select(axis => (axis.Axis, axis.Signal, axis.Hardware!.Number)).ToArray())
    {
        Axes[MachineAxis.PcbSupplyX] = axisX;
        if (axisY is not null)
            Axes[MachineAxis.PcbSupplyY] = axisY;
        if (axisZ is not null)
            Axes[MachineAxis.PcbSupplyZ] = axisZ;
    }

    public override HardwareArea Area => HardwareArea.PcbSupply;
}
