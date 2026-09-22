using System;
using IBTM.Core;
using System.Text.Json.Serialization;

namespace IBTM.Hantas;

public sealed class HantasSettings : Setting
{
    [JsonPropertyName("PortName")]
    public string PickupPortName { get; set; } = string.Empty;
    [JsonPropertyName("BaudRate")]
    public int PickupBaudRate { get; set; } = 115_200;
    public string ShootingPortName { get; set; } = string.Empty;
    public int ShootingBaudRate { get; set; } = 115_200;
    public byte PickupSlaveAddress { get; set; } = 0;
    public byte ShootingSlaveAddress { get; set; } = 1;
    public int ResponseTimeoutMilliseconds { get; set; } = 1_000;
    public int FasteningTimeoutMilliseconds { get; set; } = 15_000;
    public int ResultPollingIntervalMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 100;
}
