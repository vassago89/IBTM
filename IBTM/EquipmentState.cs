using System;
using System.Linq;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public enum EquipmentAlarm
{
    None,
    EmergencyStop,
    Supply,
    Placement,
}

public sealed class EquipmentState
{
    private readonly MachineSettings _settings;
    private readonly IIoService _io;
    private readonly IConveyorServo _conveyor;
    private readonly BufferStage _buffer;
    private readonly PcbSupplyHandler _supply;
    private readonly PcbPlacementStation _placement;
    private readonly BoltFasteningStation _boltFastening;
    private readonly InspectionStation _inspection;
    private readonly MotionService _pcbSupplyMotion;
    private readonly MotionService _pcbPlacementMotion;
    private readonly MotionService _boltFasteningMotion;
    private readonly MotionService _inspectionMotion;
    private readonly MotionService[] _motions;

    public EquipmentState(
        MachineSettings settings,
        IIoService io,
        IConveyorServo conveyor,
        BufferStage buffer,
        PcbSupplyHandler supply,
        PcbPlacementStation placement,
        BoltFasteningStation boltFastening,
        InspectionStation inspection,
        [FromKeyedServices(MotionGroup.PcbSupply)] MotionService pcbSupplyMotion,
        [FromKeyedServices(MotionGroup.PcbPlacement)] MotionService pcbPlacementMotion,
        [FromKeyedServices(MotionGroup.BoltFastening)] MotionService boltFasteningMotion,
        [FromKeyedServices(MotionGroup.Inspection)] MotionService inspectionMotion)
    {
        _settings = settings;
        _io = io;
        _conveyor = conveyor;
        _buffer = buffer;
        _supply = supply;
        _placement = placement;
        _boltFastening = boltFastening;
        _inspection = inspection;
        _pcbSupplyMotion = pcbSupplyMotion;
        _pcbPlacementMotion = pcbPlacementMotion;
        _boltFasteningMotion = boltFasteningMotion;
        _inspectionMotion = inspectionMotion;
        _motions =
        [
            pcbSupplyMotion,
            pcbPlacementMotion,
            boltFasteningMotion,
            inspectionMotion,
        ];

        io.InputChanged += (_, _) => Refresh();
        conveyor.RunningChanged += _ => Refresh();
        buffer.Changed += Refresh;
        inspection.NgCarrierCountChanged += (_, _) => Refresh();
        pcbSupplyMotion.MovingChanged += _ => Refresh();
        pcbPlacementMotion.MovingChanged += _ => Refresh();
        boltFasteningMotion.MovingChanged += _ => Refresh();
        inspectionMotion.MovingChanged += _ => Refresh();
    }

    public event Action? Changed;

    public bool Homed { get; private set; }
    public bool ServosOn { get; private set; }
    public bool Faulted { get; private set; }
    public bool Ready => Homed && ServosOn && !Faulted;

    public bool EmergencyStopReleased { get; private set; }
    public bool DoorClosed { get; private set; }
    public bool AirPressureOk { get; private set; }
    public bool SafetyReady =>
        (!_settings.Options.UseEmergencyStop || EmergencyStopReleased)
        && (!_settings.Options.UseDoorInterlock || DoorClosed)
        && (!_settings.Options.UseAirPressureInterlock || AirPressureOk);

    public bool IsError { get; private set; }
    public bool IsHoming { get; private set; }
    public EquipmentAlarm Alarm { get; private set; }

    public bool ConveyorRunning { get; private set; }
    public BufferOwner BufferOwner { get; private set; }
    public bool BufferRecoveryRequired =>
        BufferOwner != BufferOwner.None && _buffer.CancellationRequested;
    public bool SupplyInBufferArea { get; private set; }
    public bool PlacementInBufferArea { get; private set; }
    public bool BufferConflict =>
        (SupplyInBufferArea && BufferOwner != BufferOwner.Supply)
        || (PlacementInBufferArea && BufferOwner != BufferOwner.Placement);

    public bool PcbSupplyMoving { get; private set; }
    public bool PcbPlacementMoving { get; private set; }
    public bool BoltFasteningMoving { get; private set; }
    public bool InspectionMoving { get; private set; }
    public bool PcbSupplyActive =>
        PcbSupplyMoving
        || (BufferOwner == BufferOwner.Supply && !BufferRecoveryRequired);
    public bool PcbPlacementActive =>
        PcbPlacementMoving
        || (BufferOwner == BufferOwner.Placement && !BufferRecoveryRequired);
    public bool EquipmentRunning =>
        IsHoming
        || ConveyorRunning
        || (BufferOwner != BufferOwner.None && !_buffer.CancellationRequested)
        || PcbSupplyMoving
        || PcbPlacementMoving
        || BoltFasteningMoving
        || InspectionMoving;

    public int NgCarrierCount => _inspection.NgCarrierCount;
    public int NgCarrierCapacity => _inspection.NgCarrierCapacity;
    public bool NgCarrierFull =>
        NgCarrierCount >= NgCarrierCapacity;
    public bool CanOperate =>
        Ready && SafetyReady && !IsError && !NgCarrierFull;
    public bool ManualControlsEnabled =>
        CanOperate
        && BufferOwner == BufferOwner.None
        && !EquipmentRunning;
    public PcbSupplyRotation SupplyRotation => _supply.Rotation;
    public bool SupplyPcbPresent => _supply.PcbPresent;
    public bool SupplyCarrierAvailable => _supply.CarrierAvailable;
    public bool PlacementPcbPresent => _placement.PcbPresent;
    public bool PlacementOccupied => _placement.HousingPresent;
    public bool BoltCarrierJigPresent => _boltFastening.CarrierJigPresent;
    public bool InspectionCarrierJigPresent => _inspection.CarrierJigPresent;

    public void Refresh()
    {
        var axes = _motions
            .SelectMany(motion => motion.Axes.Select(motion.GetAxisState))
            .ToArray();
        var conveyor = _conveyor.GetAxisState();

        Homed = axes.All(axis => axis.Homed);
        ServosOn =
            axes.All(axis => axis.ServoOn)
            && conveyor.ServoOn;
        Faulted = axes.Any(axis =>
            axis.Alarm
            || axis.Emergency
            || axis.PositiveLimit
            || axis.NegativeLimit)
            || conveyor.Alarm
            || conveyor.Emergency
            || conveyor.PositiveLimit
            || conveyor.NegativeLimit;

        EmergencyStopReleased =
            _io.GetInput(InputIo.EmergencyStopReleased);
        DoorClosed = _io.GetInput(InputIo.DoorClosed);
        AirPressureOk = _io.GetInput(InputIo.AirPressureOk);
        ConveyorRunning = !conveyor.InPosition;
        BufferOwner = _buffer.Owner;

        var supply = _pcbSupplyMotion.GetPosition();
        var placement = _pcbPlacementMotion.GetPosition();
        var bufferSettings = _settings.PcbBuffer;
        SupplyInBufferArea =
            bufferSettings.IsConfigured
            && bufferSettings.ContainsSupply(supply.X);
        PlacementInBufferArea =
            bufferSettings.IsConfigured
            && bufferSettings.ContainsPlacement(placement.X, placement.Y);

        PcbSupplyMoving = IsMoving(_pcbSupplyMotion);
        PcbPlacementMoving = IsMoving(_pcbPlacementMotion);
        BoltFasteningMoving = IsMoving(_boltFasteningMotion);
        InspectionMoving = IsMoving(_inspectionMotion);
        Changed?.Invoke();
    }

    internal void SetHoming(bool value)
    {
        IsHoming = value;
        Changed?.Invoke();
    }

    internal void SetError(EquipmentAlarm alarm = EquipmentAlarm.None)
    {
        Alarm = alarm;
        IsError = true;
        Changed?.Invoke();
    }

    internal void ClearError()
    {
        Alarm = EquipmentAlarm.None;
        IsError = false;
        Changed?.Invoke();
    }

    private static bool IsMoving(MotionService motion) => motion.IsMoving;
}
