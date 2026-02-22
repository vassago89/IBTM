using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public class VirtualMotionService : IMotionService
    {
        private int? _axisX;
        private int? _axisY;
        private int? _axisZ;

        private double _x;
        private double _y;
        private double _z;

        private int _delayMS;

        private bool _isRun;

        private bool _servoOn;

        public VirtualMotionService()
        {
            _delayMS = 10;
        }

        public void GetPotision(out double? x, out double? y, out double? z)
        {
            x = _x * 1000;
            y = _y * 1000;
            z = _z * 1000;
        }

        public void Initialize(int? axisX, int? axisY, int? axisZ)
        {
            _axisX = axisX;
            _axisY = axisY;
            _axisZ = axisZ;

            _servoOn = true;
        }

        public async Task MoveX(double x, double velocity, params IMotionConverter[] converters)
        {
            foreach (var converter in converters)
                x = converter.To(x, 0).X;

            if (_x > x)
                velocity = -velocity;

            _isRun = true;
            await Task.Run(async () =>
            {
                if (_axisX == null)
                    return;

                while (_isRun)
                {
                    if (Move(ref _x, x, velocity))
                        break;

                    await Task.Delay(_delayMS);
                }
            });
        }
        public async Task MoveXY(double x, double y, double velocity, params IMotionConverter[] converters)
        {
            foreach (var converter in converters)
            {
                var to = converter.To(x, y);
                x = to.X;
                y = to.Y;
            }   

            var distX = Math.Abs(x - _x);
            var distY = Math.Abs(y - _y);

            if (distX + distY == 0)
                return;

            var velocityX = velocity * (double)distX / (distX + distY);
            var velocityY = velocity * (double)distY / (distX + distY);

            if (_x > x)
                velocityX = -velocityX;

            if (_y > y)
                velocityY = -velocityY;

            _isRun = true;
            await Task.Run(async () =>
            {
                if (_axisX == null || _axisY == null)
                    return;

                while (_isRun)
                {
                    var done = Move(ref _x, x, velocityX);
                    if (_isRun == false)
                        break;

                    done &= Move(ref _y, y, velocityY);
                    await Task.Delay(_delayMS);
                    if (done)
                        break;
                }
            });
        }

        public async Task MoveY(double y, double velocity, params IMotionConverter[] converters)
        {
            foreach (var converter in converters)
                y = converter.To(y, 0).Y;

            if (_y > y)
                velocity = -velocity;

            _isRun = true;
            await Task.Run(async () =>
            {
                if (_axisY == null)
                    return;

                while (_isRun)
                {
                    if (Move(ref _y, y, velocity))
                        break;

                    await Task.Delay(_delayMS);
                }
            });
        }

        public async Task MoveZ(double z, double velocity)
        {
            return;
            if (_z > z)
                velocity = -velocity;

            _isRun = true;
            await Task.Run(async () =>
            {
                if (_axisZ == null)
                    return;

                while (_isRun)
                {
                    if (Move(ref _z, z, velocity))
                        break;

                    await Task.Delay(_delayMS);
                }
            });
        }


        private bool Move(ref double current, double position, double velocity)
        {
            var distance = velocity * (_delayMS / 1000.0);

            if (distance > 0)
            {
                if (current + distance > position)
                    current = position;
                else
                    current += distance;
            }
            else
            {
                if (current + distance < position)
                    current = position;
                else
                    current += distance;
            }

            return current == position;
        }

        private async Task<double> Move(double current, double velocity)
        {
            var distance = velocity * (_delayMS / 1000.0);

            if (distance > 0)
                current += distance;
            else
                current += distance;

            await Task.Delay(_delayMS);

            return current;
        }

        public void MoveX(double velocity)
        {
            if (_axisX == null)
                return;

            _isRun = true;
            Task.Run(async () =>
            {
                while (_isRun)
                {
                    _x = await Move(_x, velocity);
                }
            });
        }

        public void MoveY(double velocity)
        {
            if (_axisY == null)
                return;

            _isRun = true;
            Task.Run(async () =>
            {
                while (_isRun)
                {
                    _y = await Move(_y, velocity);
                }
            });
        }

        public void MoveZ(double velocity)
        {
            if (_axisZ == null)
                return;

            _isRun = true;
            Task.Run(async () =>
            {
                while (_isRun)
                {
                    _z = await Move(_z, velocity);
                }
            });
        }

        public void Stop()
        {
            _isRun = false;
        }

        public void AlarmReset()
        {
            
        }

        private MotionStatus GetStatus(int axisNo)
        {
            return new MotionStatus(
                true,
                _servoOn,
                false,
                false,
                true,
                false,
                false,
                false);
        }

        public MotionStatus GetXStatus()
        {
            if (_axisX == null)
                return null;

            return GetStatus(_axisX.Value);
        }

        public MotionStatus GetYStatus()
        {
            if (_axisY == null)
                return null;

            return GetStatus(_axisY.Value);
        }

        public MotionStatus GetZStatus()
        {
            if (_axisZ == null)
                return null;

            return GetStatus(_axisZ.Value);
        }

        public async Task<bool> OriginX(double velocity)
        {
            await MoveX(0, velocity);
            return true;
        }

        public async Task<bool> OriginY(double velocity)
        {
            await MoveY(0, velocity);
            return true;
        }

        public async Task<bool> OriginZ(double velocity)
        {
            await MoveZ(0, velocity);
            return true;
        }

        public void EStop()
        {
            _isRun = false;
        }

        public void On()
        {

        }

        public void Off()
        {

        }
    }
}
