using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public interface IIOService
    {
        void Initiliaze();
        Action<int, bool> InChanged { get; set; }
        Action<int, bool> OutChanged { get; set; }

        bool GetIn(int index);
        bool GetOut(int index);

        void Set(int index, bool value);

        void Off();

        void GetAll();
    }
}
