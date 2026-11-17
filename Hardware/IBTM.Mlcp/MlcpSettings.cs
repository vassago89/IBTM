using System;
using IBTM.Core;

namespace IBTM.Mlcp;

public sealed class MlcpSettings : Setting
{
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 9600;
    public MlcpCommandFormat CommandFormat { get; set; }
    public int ResponseTimeoutMilliseconds
    {
        get;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Response timeout must be positive.");
            field = value;
        }
    } = 3000;
}
