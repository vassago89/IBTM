using System;
using System.ComponentModel;

namespace IBTM.Device;

public abstract class IoSignal<T> : INotifyPropertyChanged
    where T : struct, Enum
{
    protected IoSignal(
        T signal,
        HardwareArea area,
        IoSection? section,
        int? number = null)
    {
        Signal = signal;
        Area = area;
        Section = section;
        Number = number is >= 0 ? number : null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public T Signal { get; }
    public HardwareArea Area { get; }
    public IoSection? Section { get; }
    public int? Number { get; }

    public virtual string Address => Number?.ToString("D3") ?? "—";

    public abstract bool? IsOn { get; }

    protected void Notify(string propertyName)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
    }

    internal virtual void Refresh()
    {
        Notify(nameof(IsOn));
    }
}
