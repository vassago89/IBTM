using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace IBTM.Device;

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
            input.PropertyChanged += OnInputChanged;
    }

    private void OnInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshFeedback();
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
                : Feedback[on ? 0 : 1].IsOn == true
                    && Feedback[on ? 1 : 0].IsOn == false;
        }
    }

    internal override void Refresh()
    {
        Update(Number is not null ? _io.GetOutput(Signal) : null);
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
