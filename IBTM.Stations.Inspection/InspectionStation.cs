using System;
using IBTM.Device;

namespace IBTM.Stations.Inspection;

public sealed class InspectionStation(
    MotionService motion,
    InspectionSettings settings,
    ICamera camera,
    IIoService io)
{
    public event Action<int, bool>? NgCarrierCountChanged;

    public int NgCarrierCount { get; private set; }
    public int NgCarrierCapacity => settings.NgCarrierCapacity;
    public bool CarrierJigPresent =>
        io.GetInput(InputIo.InspectionCarrierJigPresent);

    public void Initialize()
    {
        motion.Initialize();
        camera.Initialize();
        SetLaser(false);
    }

    public void ResetNgCarrierCount()
    {
        NgCarrierCount = 0;
        NgCarrierCountChanged?.Invoke(0, false);
    }

    public void Stop()
    {
        motion.Stop();
        SetLaser(false);
    }

    public void EmergencyStop()
    {
        motion.EmergencyStop();
        SetLaser(false);
    }

    public void SetLaser(bool on) =>
        io.SetOutput(OutputIo.InspectionLaser, on);
}
