using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public class VirtualOService : IIOService
    {
        public Dictionary<int, bool> _inputs;
        public Dictionary<int, bool> _outputs;

        public Action<int, bool> InChanged { get; set; }
        public Action<int, bool> OutChanged { get; set; }

        public VirtualOService()
        {
            _inputs = new Dictionary<int, bool>();
            _outputs = new Dictionary<int, bool>();

            for (int i = 0; i < 64; i++)
            {
                _inputs[i] = false;
                _outputs[i] = false;
            }
        }

        public bool GetIn(int index)
        {
            return _inputs[index];
        }

        public bool GetOut(int index)
        {
            return _outputs[index];
        }

        public virtual void Set(int index, bool value)
        {
            _outputs[index] = value;
            Task.Run(() =>
            {
                OutChanged?.Invoke(index, value);
            }); 
        }

        public void SetInput(int index, bool value)
        {
            _inputs[index] = value;
            Task.Run(() =>
            {
                InChanged?.Invoke(index, value);
            });
        }

        public void Off()
        {

        }

        public void GetAll()
        {

        }

        public void Initiliaze()
        {

        }
    }
}
