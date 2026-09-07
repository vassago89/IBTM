using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace IBTM.Device;

public class IoSignal<T>(
    T signal,
    HardwareArea area,
    IoSection? section,
    Func<T, bool> read) : INotifyPropertyChanged
    where T : struct, Enum
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public T Signal { get; } = signal;
    public HardwareArea Area { get; } = area;
    public IoSection? Section { get; } = section;
    public bool IsOn => read(Signal);

    protected void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    internal virtual void Refresh() => Notify(nameof(IsOn));
}

public sealed class IoOutputStatus : IoSignal<OutputIo>
{
    public IoOutputStatus(
        OutputIo signal,
        HardwareArea area,
        IoSection? section,
        IIoService io,
        IReadOnlyDictionary<InputIo, IoSignal<InputIo>> inputs)
        : base(signal, area, section, io.GetOutput)
    {
        Feedback = io.GetOutputFeedback(signal) is { } feedback
            ? [inputs[feedback.OnInput], inputs[feedback.OffInput]]
            : [];
        foreach (var input in Feedback)
            input.PropertyChanged += (_, _) => RefreshFeedback();
    }

    public IoSignal<InputIo>[] Feedback { get; }
    public bool HasFeedback => Feedback.Length > 0;
    public bool HasConflict => HasFeedback && Feedback[0].IsOn && Feedback[1].IsOn;
    public bool IsMatched => HasFeedback && !HasConflict && Feedback[IsOn ? 0 : 1].IsOn;

    internal override void Refresh()
    {
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
