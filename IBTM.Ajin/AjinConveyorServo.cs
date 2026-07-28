using System;
using IBTM.Device;

namespace IBTM.Ajin;

public sealed class AjinConveyorServo(
    AjinController controller,
    HardwareMap hardware) : IConveyorServo
{
    private readonly int _axis = hardware.Axes[MachineAxis.Conveyor];
    private readonly int _direction =
        (int)hardware.AxisDirections[MachineAxis.Conveyor];

    public event Action<bool>? RunningChanged;

    public void Initialize()
    {
        controller.Initialize();
        AjinController.Check(
            AjinNative.AxmSignalServoOn(_axis, 1),
            nameof(AjinNative.AxmSignalServoOn));
    }

    public void Run(double velocity)
    {
        var velocityInUnits =
            velocity * _direction * controller.Settings.UnitsPerMillimeter;
        var acceleration = Math.Abs(velocityInUnits)
                           * controller.Settings.AccelerationMultiplier;
        AjinController.Check(
            AjinNative.AxmMoveVel(
                _axis,
                velocityInUnits,
                acceleration,
                acceleration),
            nameof(AjinNative.AxmMoveVel));
        RunningChanged?.Invoke(true);
    }

    public void Stop()
    {
        AjinController.Check(
            AjinNative.AxmMoveSStop(_axis),
            nameof(AjinNative.AxmMoveSStop));
        RunningChanged?.Invoke(false);
    }

    public void EmergencyStop()
    {
        AjinController.Check(
            AjinNative.AxmMoveEStop(_axis),
            nameof(AjinNative.AxmMoveEStop));
        RunningChanged?.Invoke(false);
    }

    public void SetServo(bool on) =>
        AjinController.Check(
            AjinNative.AxmSignalServoOn(_axis, on ? 1U : 0U),
            nameof(AjinNative.AxmSignalServoOn));

    public AxisState GetAxisState()
    {
        var mechanical = 0U;
        var servoOn = 0U;
        AjinController.Check(
            AjinNative.AxmStatusReadMechanical(_axis, ref mechanical),
            nameof(AjinNative.AxmStatusReadMechanical));
        AjinController.Check(
            AjinNative.AxmSignalIsServoOn(_axis, ref servoOn),
            nameof(AjinNative.AxmSignalIsServoOn));
        var positiveLimit = Bit(mechanical, 0);
        var negativeLimit = Bit(mechanical, 1);

        return new AxisState(
            Homed: true,
            ServoOn: servoOn != 0,
            Alarm: Bit(mechanical, 4),
            InPosition: Bit(mechanical, 5),
            Emergency: Bit(mechanical, 6),
            HomeSensor: Bit(mechanical, 7),
            PositiveLimit: _direction > 0 ? positiveLimit : negativeLimit,
            NegativeLimit: _direction > 0 ? negativeLimit : positiveLimit);
    }

    public void ResetAlarm() =>
        AjinController.Check(
            AjinNative.AxmSignalServoAlarmReset(_axis, 1),
            nameof(AjinNative.AxmSignalServoAlarmReset));

    private static bool Bit(uint value, int bit) =>
        ((value >> bit) & 1) != 0;
}
