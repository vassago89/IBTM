using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public class MotionStatus : IEquatable<MotionStatus>
    {
        public bool IsOriginDone { get; }

        public bool IsServoOn { get; }

        public bool IsLimitPositive { get; }
        public bool IsLimitNegative { get; }
        public bool IsAlarm { get; }
        public bool IsInposition { get; }
        public bool IsEmergency { get; }
        public bool IsHome { get; }

        public MotionStatus(
            bool isOriginDone,
            bool isServoOn,
            bool isEmergency,
            bool isAlarm,
            bool isInposition,
            bool isHome,
            bool isLimitPositive,
            bool isLimitNegative)
        {
            this.IsOriginDone = isOriginDone;

            this.IsServoOn = isServoOn;
            this.IsEmergency = isEmergency;
            this.IsAlarm = isAlarm;
            this.IsInposition = isInposition;
            this.IsHome = isHome;
            this.IsLimitPositive = isLimitPositive;
            this.IsLimitNegative = isLimitNegative;
        }

        public bool Equals(MotionStatus? other)
        {
            if (other == null)
                return false;

            return this.IsServoOn == other.IsServoOn
                && this.IsEmergency == other.IsEmergency
                && this.IsAlarm == other.IsAlarm
                && this.IsInposition == other.IsInposition
                && this.IsHome == other.IsHome
                && this.IsLimitPositive == other.IsLimitPositive
                && this.IsLimitNegative == other.IsLimitNegative;
        }
    }
}
