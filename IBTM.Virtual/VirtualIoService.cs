using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;

namespace IBTM.Virtual;

public sealed class VirtualIoService : IIoService
{
    private static readonly int[] CarrierJigPresentInputChannels =
    [
        PcbPlacementStation.CarrierJigPresentInputChannel,
        BoltFasteningStation.CarrierJigPresentInputChannel,
        InspectionStation.CarrierJigPresentInputChannel,
    ];

    private static readonly int[] StopperUpOutputChannels =
    [
        PcbPlacementStation.StopperUpOutputChannel,
        BoltFasteningStation.StopperUpOutputChannel,
        InspectionStation.StopperUpOutputChannel,
    ];

    private static readonly int[] FeedbackChannels =
    [
        PcbPlacementStation.GripperChannel,
        InspectionStation.GripperChannel,
    ];

    private readonly bool[] _inputs = new bool[64];
    private readonly bool[] _outputs = new bool[64];
    private bool _conveyorRunning;

    private event Action<int, bool>? InputChanged;

    public void Initialize()
    {
        SetInput(PcbPlacementStation.PcbAvailableInputChannel, true);
        SetInput(Conveyor.UpstreamBoardAvailableInputChannel, true);
        SetInput(Conveyor.DownstreamMachineReadyInputChannel, true);
        foreach (var channel in CarrierJigPresentInputChannels)
        {
            SetInput(channel, false);
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

        var stationIndex = Array.IndexOf(StopperUpOutputChannels, channel);
        if (stationIndex >= 0 && value && _conveyorRunning)
        {
            ReleaseCarrierJig(stationIndex);
        }
        else if (channel == Conveyor.UpstreamMachineReadyOutputChannel && !value)
        {
            SetInput(Conveyor.UpstreamBoardAvailableInputChannel, true);
        }

        if (channel == InspectionStation.GripperChannel
            && value
            && _outputs[InspectionStation.BackupPlateUpOutputChannel]
            && _inputs[InspectionStation.CarrierJigPresentInputChannel])
        {
            SetInput(InspectionStation.CarrierJigPresentInputChannel, false);
        }

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

    private void ReleaseCarrierJig(int stationIndex)
    {
        var currentInputChannel = CarrierJigPresentInputChannels[stationIndex];
        if (!_inputs[currentInputChannel])
        {
            throw new InvalidOperationException($"Station {stationIndex + 1} has no carrier jig.");
        }

        if (stationIndex < CarrierJigPresentInputChannels.Length - 1)
        {
            var destinationInputChannel = CarrierJigPresentInputChannels[stationIndex + 1];
            if (_inputs[destinationInputChannel])
            {
                throw new InvalidOperationException($"Station {stationIndex + 2} is occupied.");
            }
        }

        SetInput(currentInputChannel, false);

        if (stationIndex < CarrierJigPresentInputChannels.Length - 1)
        {
            SetInput(CarrierJigPresentInputChannels[stationIndex + 1], true);
        }

    }

    internal void StartConveyor()
    {
        _conveyorRunning = true;

        if (_outputs[Conveyor.UpstreamMachineReadyOutputChannel]
            && _inputs[Conveyor.UpstreamBoardAvailableInputChannel]
            && !_inputs[PcbPlacementStation.CarrierJigPresentInputChannel])
        {
            SetInput(Conveyor.UpstreamBoardAvailableInputChannel, false);
            SetInput(PcbPlacementStation.CarrierJigPresentInputChannel, true);
        }
    }

    internal void StopConveyor() => _conveyorRunning = false;
}
