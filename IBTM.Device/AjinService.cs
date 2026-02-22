using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public partial class AjinMotionService : IMotionService
    {
        private int? _axisX;
        private int? _axisY;
        private int? _axisZ;

        public AjinMotionService()
        {

        }


        public static void Initialize()
        {
            String szFilePath = "Settings/Default.mot";

            CAXL.AxlClose();
            if (CAXL.AxlOpen(7) != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
                throw new Exception("Intialize Fail..!!");

            if (CAXM.AxmMotLoadParaAll(szFilePath) != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
                throw new Exception("Mot File Not Found.");
        }

        public void Initialize(int? axisX, int? axisY, int? axisZ)
        {
            _axisX = axisX;
            _axisY = axisY;
            _axisZ = axisZ;
        }

        public void On()
        {
            if (_axisX != null)
                CAXM.AxmSignalServoOn(_axisX.Value, 1);

            if (_axisY != null)
                CAXM.AxmSignalServoOn(_axisY.Value, 1);

            if (_axisZ != null)
                CAXM.AxmSignalServoOn(_axisZ.Value, 1);
        }

        public void Off()
        {
            if (_axisX != null)
                CAXM.AxmSignalServoOn(_axisX.Value, 0);

            if (_axisY != null)
                CAXM.AxmSignalServoOn(_axisY.Value, 0);

            if (_axisZ != null)
                CAXM.AxmSignalServoOn(_axisZ.Value, 0);
        }

        public Task MoveX(double x, double velocity, params IMotionConverter[] converters)
        {
            foreach (var converter in converters)
                x = converter.To(x, 0).X;

            return Task.Run(() =>
            {
                if (_axisX == null)
                    return;

                var vleocityUM = velocity * 1000;

                CAXM.AxmMovePos(_axisX.Value, x * 1000, vleocityUM, vleocityUM * 2, vleocityUM * 2);
            });
        }

        public void MoveX(double velocity)
        {
            if (_axisX == null)
                return;

            var vleocityUM = velocity * 1000;

            CAXM.AxmMoveVel(_axisX.Value, vleocityUM, vleocityUM * 2, vleocityUM * 2);
        }

        public Task MoveXY(double x, double y, double velocity, params IMotionConverter[] converters)
        {
            foreach (var converter in converters)
            {
                var to = converter.To(x, y);
                x = to.X;
                y = to.Y;
            }

            GetPotision(out double? _x, out double? _y, out double? _z);
            var distX = Math.Abs(x - (_x.Value / 1000));
            var distY = Math.Abs(y - (_y.Value / 1000));

            var velocityX = velocity * ((double)distX / (distX + distY)) * 1000;
            var velocityY = velocity * ((double)distY / (distX + distY)) * 1000;

            return Task.Run(() =>
            {
                if (_axisX == null || _axisY == null)
                    return;

                CAXM.AxmMoveMultiPos(
                    2,
                    [ _axisX.Value, _axisY.Value ],
                    [ x * 1000,  y * 1000 ],
                    [ velocityX, velocityY],
                    [ velocityX * 2, velocityY * 2],
                    [ velocityX * 2, velocityY * 2]);
            });
        }

        public Task MoveY(double y, double velocity, params IMotionConverter[] converters)
        {
            foreach (var converter in converters)
                y = converter.To(y, 0).Y;

            return Task.Run(() =>
            {
                if (_axisY == null)
                    return;

                var vleocityUM = velocity * 1000;
                CAXM.AxmMovePos(_axisY.Value, y * 1000, vleocityUM, vleocityUM * 2, vleocityUM * 2);
            });
        }

        public void MoveY(double velocity)
        {
            if (_axisY == null)
                return;

            var vleocityUM = velocity * 1000;
            CAXM.AxmMoveVel(_axisY.Value, vleocityUM, vleocityUM * 2, vleocityUM * 2);
        }

        public Task MoveZ(double z, double velocity)
        {
            return Task.Run(() =>
            {
                if (_axisZ == null)
                    return;

                var vleocityUM = velocity * 1000;
                CAXM.AxmMovePos(_axisZ.Value, z * 1000, vleocityUM, vleocityUM * 2, vleocityUM * 2);
            });
        }

        public void MoveZ(double velocity)
        {
            if (_axisZ == null)
                return;
            
            var vleocityUM = velocity * 1000;
            CAXM.AxmMoveVel(_axisZ.Value, vleocityUM, vleocityUM * 2, vleocityUM * 2);
        }

        public void Stop()
        {
            if (_axisX != null)
                CAXM.AxmMoveSStop(_axisX.Value);

            if (_axisY != null)
                CAXM.AxmMoveSStop(_axisY.Value);

            if (_axisZ != null)
                CAXM.AxmMoveSStop(_axisZ.Value);
        }

        public void EStop()
        {
            if (_axisX != null)
                CAXM.AxmMoveEStop(_axisX.Value);

            if (_axisY != null)
                CAXM.AxmMoveEStop(_axisY.Value);

            if (_axisZ != null)
                CAXM.AxmMoveEStop(_axisZ.Value);
        }

        public void GetPotision(out double? x, out double? y, out double? z)
        {
            if (_axisX != null)
            {
                x = GetPosition(_axisX.Value);
            }
            else
            {
                x = null;
            }

            if (_axisY != null)
            {
                y = GetPosition(_axisY.Value);
            }
            else
            {
                y = null;
            }

            if (_axisZ != null)
            {
                z = GetPosition(_axisZ.Value);
            }
            else
            {
                z = null;
            }
        }

        private double? GetPosition(int axisNo)
        {
            double position = 0;
            CAXM.AxmStatusGetActPos(axisNo, ref position);
            return position;
        }

        private MotionStatus GetStatus(int axisNo)
        {
            uint status = 0;
            if (CAXM.AxmStatusReadMechanical(axisNo, ref status) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
            {
                uint homeResult = 0;
                CAXM.AxmHomeGetResult(axisNo, ref homeResult);
                
                uint isServoOn = 0;
                CAXM.AxmSignalIsServoOn(axisNo, ref isServoOn);
                var motionStatus = new MotionStatus(
                    homeResult == (uint)AXT_MOTION_HOME_RESULT.HOME_SUCCESS,
                    Convert.ToBoolean(isServoOn),
                    Convert.ToBoolean((int)status >> 6 & 0x1),
                    Convert.ToBoolean((int)status >> 4 & 0x1),
                    Convert.ToBoolean((int)status >> 5 & 0x1),
                    Convert.ToBoolean((int)status >> 7 & 0x1),
                    Convert.ToBoolean((int)status >> 0 & 0x1),
                    Convert.ToBoolean((int)status >> 1 & 0x1));

                return motionStatus;
            }

            return null;
        }

        public void AlarmReset()
        {
            if (_axisX != null)
                CAXM.AxmSignalServoAlarmReset(_axisX.Value, 1);

            if (_axisY != null)
                CAXM.AxmSignalServoAlarmReset(_axisY.Value, 1);

            if (_axisZ != null)
                CAXM.AxmSignalServoAlarmReset(_axisZ.Value, 1);
        }

        public MotionStatus GetXStatus()
        {
            if (_axisX != null)
                return GetStatus(_axisX.Value);

            return null;
        }

        public MotionStatus GetYStatus()
        {
            if (_axisY != null)
                return GetStatus(_axisY.Value);

            return null;
        }

        public MotionStatus GetZStatus()
        {
            if (_axisZ != null)
                return GetStatus(_axisZ.Value);

            return null;
        }

        public async Task<bool> OriginX(double velocity)
        {
            velocity *= 1000;

            if (_axisX != null
                && CAXM.AxmHomeSetResult(_axisX.Value, (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_UNKNOWN) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                && CAXM.AxmHomeSetVel(_axisX.Value, velocity, velocity / 5, velocity / 10, velocity / 100, velocity, velocity / 10) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                && CAXM.AxmHomeSetStart(_axisX.Value) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
            {
                return await Task.Run<bool>(async () =>
                {
                    while (true)
                    {
                        uint result = 0;
                        CAXM.AxmHomeGetResult(_axisX.Value, ref result);
                        if (result == (uint)AXT_MOTION_HOME_RESULT.HOME_SUCCESS)
                            return true;

                        if (result != (uint)AXT_MOTION_HOME_RESULT.HOME_SEARCHING)
                            return false;

                        await Task.Delay(100);
                    }
                });
            }

            return false;
        }

        public async Task<bool> OriginY(double velocity)
        {
            velocity *= 1000;

            if (_axisY != null
                && CAXM.AxmHomeSetResult(_axisY.Value, (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_UNKNOWN) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                && CAXM.AxmHomeSetVel(_axisY.Value, velocity, velocity / 5, velocity / 10, velocity / 100, velocity, velocity / 10) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                && CAXM.AxmHomeSetStart(_axisY.Value) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
            {
                return await Task.Run<bool>(async () =>
                {
                    while (true)
                    {
                        uint result = 0;
                        CAXM.AxmHomeGetResult(_axisY.Value, ref result);
                        if (result == (uint)AXT_MOTION_HOME_RESULT.HOME_SUCCESS)
                            return true;

                        if (result != (uint)AXT_MOTION_HOME_RESULT.HOME_SEARCHING)
                            return false;

                        await Task.Delay(100);
                    }
                });
            }

            return false;
        }

        public async Task<bool> OriginZ(double velocity)
        {
            velocity *= 1000;
            if (_axisZ != null
                && CAXM.AxmHomeSetResult(_axisZ.Value, (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_UNKNOWN) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                && CAXM.AxmHomeSetMethod(_axisZ.Value, 0, 4, 0, 1000, 0) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                && CAXM.AxmHomeSetVel(_axisZ.Value, velocity, velocity / 5, velocity / 10, velocity / 100, velocity, velocity / 10) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                && CAXM.AxmHomeSetStart(_axisZ.Value) == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
            {
                return await Task.Run<bool>(async () =>
                {
                    uint result = 0;
                    while (true)
                    {
                        CAXM.AxmHomeGetResult(_axisZ.Value, ref result);
                        if (result == (uint)AXT_MOTION_HOME_RESULT.HOME_SUCCESS)
                            return true;

                        if (result != (uint)AXT_MOTION_HOME_RESULT.HOME_SEARCHING)
                            return false;
                        
                        await Task.Delay(100);
                    }
                });
            }

            return false;
        }
    }
}
