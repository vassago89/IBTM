using IBTM.Device;

namespace IBTM.UI;

public sealed class OutputFeedbackRow(
    OutputIo output,
    OutputFeedback feedback)
{
    public OutputIo Output { get; } = output;
    public string OnFeedback =>
        $"{feedback.OnInput} = {(feedback.OnValue ? "ON" : "OFF")}";
    public string OffFeedback =>
        $"{feedback.OffInput} = {(feedback.OffValue ? "ON" : "OFF")}";

    public int TimeoutMilliseconds
    {
        get => feedback.TimeoutMilliseconds;
        set => feedback.TimeoutMilliseconds = value;
    }
}
