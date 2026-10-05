using IBTM.Core;
using System;
using System.Text.Json.Serialization;

namespace IBTM.Hantas;

public sealed class HantasSettings : Setting
{
    public HantasSettings()
    {
        SdkCompletionTimeoutMilliseconds = 3_000;
    }

    [JsonPropertyName("PortName")]
    public string PickupPortName { get; set; } = string.Empty;
    [JsonPropertyName("BaudRate")]
    public int PickupBaudRate { get; set; } = 115_200;
    public string ShootingPortName { get; set; } = string.Empty;
    public int ShootingBaudRate { get; set; } = 115_200;
    public byte PickupSlaveAddress { get; set; } = 0;
    public byte ShootingSlaveAddress { get; set; } = 1;
    // These values also set the READY wait window in AdcBoltHead.
    // HComm owns each 1 s request timeout; ReadAttempts bounds read timeout/CRC retries.
    public int ResponseTimeoutMilliseconds { get; set; } = 1_000;
    // Application wait for the SDK callback; HComm's own request timeout is fixed at 1 s.
    public int SdkCompletionTimeoutMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }
    public int ReadAttempts
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 3;
    public int FasteningTimeoutMilliseconds { get; set; } = 15_000;
    public int PresetSettleMilliseconds { get; set; } = 200;
    public int StatusPollMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 100;
}
