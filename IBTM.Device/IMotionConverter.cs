using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public interface IMotionConverter
    {
        (double X, double Y) To(double x, double y);
        (double X, double Y) From(double x, double y);
    }
}
