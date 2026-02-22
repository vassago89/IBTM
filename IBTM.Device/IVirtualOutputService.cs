using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public interface IVirtualOutputService
    {
        void Out(int channel, double? value);
        void Stop();
        void Stop(int chnnel);

        double Read(int channel);
    }
}
