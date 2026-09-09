using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace IBTM.Device;

public class IoSignal<T>(
    T signal,
    HardwareArea area,
    IoSection? section,
    Func<T, bool> read,
    int? number = null,
    Func<bool>? available = null) : INotifyPropertyChanged
    where T : struct, Enum
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public T Signal { get; } = signal;
    public HardwareArea Area { get; } = area;
    public IoSection? Section { get; } = section;
    public int? Number { get; } = number;
    public virtual string Address => Number?.ToString("D3") ?? "—";
    public virtual bool? IsOn => available?.Invoke() == false ? null : read(Signal);

    protected void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    internal virtual void Refresh() => Notify(nameof(IsOn));
}

public sealed class IoOutputStatus : IoSignal<OutputIo>
{
    private bool? _isOn;

    public IoOutputStatus(
        OutputIo signal,
        HardwareArea area,
        IoSection? section,
        IIoService io,
        IReadOnlyDictionary<InputIo, IoSignal<InputIo>> inputs,
        int? number = null,
        int? offNumber = null)
        : base(signal, area, section, io.GetOutput, number)
    {
        OffNumber = offNumber;
        Feedback = io.GetOutputFeedback(signal) is { } feedback
            ? [inputs[feedback.OnInput], inputs[feedback.OffInput]]
            : [];
        foreach (var input in Feedback)
            input.PropertyChanged += (_, _) => RefreshFeedback();
    }

    public IoSignal<InputIo>[] Feedback { get; }
    public int? OffNumber { get; }
    public override string Address => OffNumber is { } off ? $"{base.Address} / {off:D3}" : base.Address;
    public override bool? IsOn => _isOn;
    public bool HasFeedback => Feedback.Length > 0;
    public bool HasConflict => HasFeedback && Feedback[0].IsOn == true && Feedback[1].IsOn == true;
    public bool IsMatched => IsOn is { } on && HasFeedback && !HasConflict
        && Feedback[on ? 0 : 1].IsOn == true;

    internal override void Refresh() => Update(base.IsOn);

    internal void Update(bool? value)
    {
        if (_isOn == value) return;
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

public sealed class IoStatus
{
    public IoStatus(
        HardwareArea area,
        IEnumerable<IoSignal<InputIo>> inputs,
        IEnumerable<IoOutputStatus> outputs)
    {
        Area = area;
        Outputs = outputs.OrderBy(row => row.Signal).ToArray();
        var feedback = Outputs.SelectMany(row => row.Feedback).ToHashSet();
        Inputs = inputs.Concat(feedback).Distinct().OrderBy(row => row.Signal).ToArray();
        Sensors = Inputs.Where(row => !feedback.Contains(row)).ToArray();
    }

    public HardwareArea Area { get; }
    public IReadOnlyList<IoSignal<InputIo>> Inputs { get; }
    public IReadOnlyList<IoSignal<InputIo>> Sensors { get; }
    public IReadOnlyList<IoOutputStatus> Outputs { get; }
}
