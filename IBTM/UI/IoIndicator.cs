using System;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class IoIndicator : ObservableObject
{
    private readonly Func<IIoService, bool> _read;

    public IoIndicator(string name, InputIo input, int channel)
    {
        Name = name;
        Code = $"I{channel:00}";
        _read = io => io.GetInput(input);
    }

    public IoIndicator(string name, OutputIo output, int channel)
    {
        Name = name;
        Code = $"Q{channel:00}";
        IsOutput = true;
        _read = io => io.GetOutput(output);
    }

    public string Name { get; }
    public string Code { get; }
    public bool IsOutput { get; }

    [ObservableProperty]
    private bool _isOn;

    public void Refresh(IIoService io) => IsOn = _read(io);
}
