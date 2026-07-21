using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;

namespace IBTM.Virtual;

public sealed class VirtualIoService : IIoService
{
    private static readonly int[] ReadyInputs =
    [
        PcbPlacementStation.ShuttlePresentInputChannel,
        PcbPlacementStation.PcbAvailableInputChannel,
        BoltFasteningStation.ShuttlePresentInputChannel,
        InspectionStation.ShuttlePresentInputChannel,
        InspectionStation.SmemaReadyInputChannel,
    ];

    private static readonly int[] FeedbackChannels =
    [
        PcbPlacementStation.StopperChannel,
        PcbPlacementStation.AlignChannel,
        PcbPlacementStation.LiftChannel,
        PcbPlacementStation.GripperChannel,
        BoltFasteningStation.StopperChannel,
        BoltFasteningStation.AlignChannel,
        BoltFasteningStation.LiftChannel,
        InspectionStation.StopperChannel,
        InspectionStation.AlignChannel,
        InspectionStation.LiftChannel,
        InspectionStation.GripperChannel,
    ];

    private readonly bool[] _inputs = new bool[64];
    private readonly bool[] _outputs = new bool[64];

    private event Action<int, bool>? InputChanged;

    public void Initialize()
    {
        foreach (var channel in ReadyInputs)
        {
            SetInput(channel, true);
        }
    }

    public bool GetInput(int channel) => _inputs[channel];

    public bool GetOutput(int channel) => _outputs[channel];

    public async Task WaitForInputAsync(
        int channel,
        bool value,
        CancellationToken cancellationToken = default)
    {
        if (_inputs[channel] == value)
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnInputChanged(int changedChannel, bool changedValue)
        {
            if (changedChannel == channel && changedValue == value)
            {
                completion.TrySetResult();
            }
        }

        InputChanged += OnInputChanged;
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        try
        {
            if (_inputs[channel] == value)
            {
                completion.TrySetResult();
            }

            await completion.Task;
        }
        finally
        {
            InputChanged -= OnInputChanged;
        }
    }

    public void SetInput(int channel, bool value)
    {
        _inputs[channel] = value;
        InputChanged?.Invoke(channel, value);
    }

    public void SetOutput(int channel, bool value)
    {
        _outputs[channel] = value;

        foreach (var feedbackChannel in FeedbackChannels)
        {
            if (feedbackChannel == channel)
            {
                SetInput(channel, value);
                return;
            }
        }
    }

    public void TurnOffAll()
    {
        for (var channel = 0; channel < _outputs.Length; channel++)
        {
            if (_outputs[channel])
            {
                SetOutput(channel, false);
            }
        }
    }
}
