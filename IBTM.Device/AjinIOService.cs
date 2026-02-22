using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public partial class AjinIOService : IIOService
    {
        public Action<int, bool> InChanged { get; set; }
        public Action<int, bool> OutChanged { get; set; }

        private bool[] _inputs;
        private bool[] _outputs;

        private int _inputOffset = 0;
        private int _outputOffset = 0;

        public AjinIOService()
        {
            
        }

        public void Initiliaze()
        {
            _inputs = new bool[64];
            _outputs = new bool[64];

            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        bool[] inputs = new bool[64];
                        for (int no = 0; no < 2; no++)
                        {
                            uint data = 0;
                            CAXD.AxdiReadInportDword(no + _inputOffset, 0, ref data);

                            for (int i = 0; i < 32; i++)
                            {
                                if (((data >> i) & 0x1) > 0)
                                {
                                    inputs[i + (no * 32)] = true;
                                }
                                else
                                {
                                    inputs[i + (no * 32)] = false;
                                }
                            }
                        }

                        bool[] outputs = new bool[64];
                        for (int no = 0; no < 2; no++)
                        {
                            uint data = 0;
                            CAXD.AxdoReadOutportDword(no + _outputOffset, 0, ref data);

                            for (int i = 0; i < 32; i++)
                            {
                                if (((data >> i) & 0x1) > 0)
                                {
                                    outputs[i + (no * 32)] = true;
                                }
                                else
                                {
                                    outputs[i + (no * 32)] = false;
                                }
                            }
                        }

                        for (int i = 0; i < 64; i++)
                        {
                            if (_inputs[i] != inputs[i])
                            {
                                _inputs[i] = inputs[i];
                                InChanged?.Invoke(i, _inputs[i]);
                            }
                        }

                        for (int i = 0; i < 64; i++)
                        {
                            if (_outputs[i] != outputs[i])
                            {
                                _outputs[i] = outputs[i];
                                OutChanged?.Invoke(i, _outputs[i]);
                            }
                        }

                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {

                    }
                }
            });
        }

        public bool GetIn(int index)
        {
            int cardIndex = index / 32;

            uint @value = 0;
            CAXD.AxdiReadInportBit(cardIndex + _inputOffset, index % 32, ref @value);

            return @value > 0;
        }

        public bool GetOut(int index)
        {
            int cardIndex = index / 32;

            uint @value = 0;
            CAXD.AxdoReadOutportBit(cardIndex + _outputOffset, index % 32, ref @value);

            return @value > 0;
        }

        public void Set(int index, bool value)
        {
            int cardIndex = index / 32;

            CAXD.AxdoWriteOutportBit(cardIndex + _outputOffset, index % 32, value ? (uint)1 : 0);
        }

        public void Off()
        {
            for (int i = 0; i < 32; i++)
                CAXD.AxdoWriteOutportBit(_outputOffset, i, 0);

            for (int i = 0; i < 32; i++)
                CAXD.AxdoWriteOutportBit(_outputOffset + 1, i, 0);
        }

        public void GetAll()
        {
            bool[] inputs = new bool[64];
            for (int no = 0; no < 2; no++)
            {
                uint data = 0;
                CAXD.AxdiReadInportDword(no + _inputOffset, 0, ref data);

                for (int i = 0; i < 32; i++)
                {
                    if (((data >> i) & 0x1) > 0)
                    {
                        inputs[i + (no * 32)] = true;
                    }
                    else
                    {
                        inputs[i + (no * 32)] = false;
                    }
                }
            }

            bool[] outputs = new bool[64];
            for (int no = 0; no < 2; no++)
            {
                uint data = 0;
                CAXD.AxdoReadOutportDword(no + _outputOffset, 0, ref data);

                for (int i = 0; i < 32; i++)
                {
                    if (((data >> i) & 0x1) > 0)
                    {
                        outputs[i + (no * 32)] = true;
                    }
                    else
                    {
                        outputs[i + (no * 32)] = false;
                    }
                }
            }

            for (int i = 0; i < 64; i++)
            {
                InChanged?.Invoke(
                    i,
                    inputs[i]);

                OutChanged?.Invoke(
                    i,
                    outputs[i]);
            }
        }
    }
}
