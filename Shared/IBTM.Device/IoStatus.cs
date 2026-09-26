using System;
using System.Collections.Generic;
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
        Number = number;
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

public sealed class IoInputStatus : IoSignal<InputIo>
{
    private readonly IIoService _io;

    public IoInputStatus(
        InputIo signal,
        HardwareArea area,
        IoSection? section,
        IIoService io,
        int? number = null)
        : base(signal, area, section, number)
    {
        _io = io;
    }

    public override bool? IsOn => Number is not null && _io.IsReady ? _io.GetInput(Signal) : null;
}

public sealed class IoOutputStatus : IoSignal<OutputIo>
{
    private readonly IIoService _io;
    private bool? _isOn;

    public IoOutputStatus(
        OutputIo signal,
        HardwareArea area,
        IoSection? section,
        IIoService io,
        IReadOnlyDictionary<InputIo, IoInputStatus> inputs,
        int? number = null,
        int? offNumber = null) : base(signal, area, section, number)
    {
        _io = io;
        OffNumber = offNumber;
        Feedback = io.GetOutputFeedback(signal) is { } feedback
            ? feedback.OffInput is { } offInput
                ? [inputs[feedback.OnInput], inputs[offInput]]
                : [inputs[feedback.OnInput]]
            : [];
        foreach (var input in Feedback)
            input.PropertyChanged += (_, _) => RefreshFeedback();
    }

    public IoInputStatus[] Feedback { get; }
    public int? OffNumber { get; }

    public override string Address
    {
        get
        {
            if (OffNumber is not { } off)
                return base.Address;
            return off < 0 ? $"{base.Address} / —" : $"{base.Address} / {off:D3}";
        }
    }

    public override bool? IsOn => _isOn;

    public bool HasFeedback => Feedback.Length > 0;

    public bool HasConflict => Feedback.Length == 2 && Feedback[0].IsOn == true && Feedback[1].IsOn == true;

    public bool IsMatched
    {
        get
        {
            if (IsOn is not { } on || !HasFeedback || HasConflict)
                return false;
            return Feedback.Length == 1
                ? Feedback[0].IsOn == on
                : Feedback[on ? 0 : 1].IsOn == true;
        }
    }

    internal override void Refresh()
    {
        Update(_io.GetOutput(Signal));
    }

    internal void Update(bool? value)
    {
        if (_isOn == value)
            return;
        _isOn = value;
        base.Refresh();
        RefreshFeedback();
    }

    private void RefreshFeedback()
    {
        Notify(nameof(HasConflict));
        Notify(nameof(IsMatched));
    }
}
