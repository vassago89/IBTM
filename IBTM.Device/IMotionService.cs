using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public interface IMotionService
    {
        void Initialize(int? axisX, int? axisY, int? axisZ);

        void On();

        void Off();

        Task MoveXY(double x, double y, double velocity, params IMotionConverter[] converters);

        Task MoveX(double x, double velocity, params IMotionConverter[] converters);

        Task MoveY(double y, double velocity, params IMotionConverter[] converters);

        Task MoveZ(double z, double velocity);

        void MoveX(double velocity);

        void MoveY(double velocity);

        void MoveZ(double velocity);

        void Stop();
        void EStop();

        void GetPotision(out double? x, out double? y, out double? z);

        Task<bool> OriginX(double velocity);
        Task<bool> OriginY(double velocity);
        Task<bool> OriginZ(double velocity);

        void AlarmReset();

        MotionStatus GetXStatus();
        MotionStatus GetYStatus();
        MotionStatus GetZStatus();
    }
}
