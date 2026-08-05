using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualIoService(HardwareMap? hardware = null) : IIoService
{
    private const int FeedbackDelayMilliseconds = 200;

    private static readonly (InputIo? Carrier, InputIo Housing1, InputIo Housing2)[] Stations =
    [
        (
            null,
            InputIo.PcbPlacementHousing1Present,
            InputIo.PcbPlacementHousing2Present),
        (
            InputIo.BoltFasteningCarrierJigPresent,
            InputIo.BoltFasteningHousing1Present,
            InputIo.BoltFasteningHousing2Present),
        (
            InputIo.InspectionCarrierJigPresent,
            InputIo.InspectionHousing1Present,
            InputIo.InspectionHousing2Present),
    ];

    private static readonly OutputIo[] StopperUpOutputs =
    [
        OutputIo.PcbPlacementStopperUp,
        OutputIo.BoltFasteningStopperUp,
        OutputIo.InspectionStopperUp,
    ];

    private readonly bool[] _inputs = new bool[
        Enum.GetValues<InputIo>().Max(input => (int)input) + 1];
    private readonly bool[] _outputs = new bool[
        Enum.GetValues<OutputIo>().Max(output => (int)output) + 1];
    private readonly int[] _feedbackVersions = new int[
        Enum.GetValues<OutputIo>().Max(output => (int)output) + 1];
    private readonly bool[] _stationOccupied = new bool[Stations.Length];
    private bool _conveyorRunning;

    public event Action<InputIo, bool>? InputChanged;
    public event Action<OutputIo, bool>? OutputChanged;

    public HardwareMap Hardware { get; } = hardware ?? new HardwareMap();
    public bool SupplyPcbPresentOnPick { get; set; } = true;
    public bool PlacementPcbPresentOnPick { get; set; } = true;

    public void Initialize()
    {
        SetInput(InputIo.ConveyorUpstreamBoardAvailable, true);
        SetInput(InputIo.ConveyorDownstreamMachineReady, true);
        SetInput(InputIo.EmergencyStopReleased, true);
        SetInput(InputIo.DoorClosed, true);
        SetInput(InputIo.AirPressureOk, true);
        SetInput(InputIo.PcbSupplyUnrotated, true);
    }

    public bool GetInput(InputIo input) => _inputs[(int)input];

    public bool GetOutput(OutputIo output) => _outputs[(int)output];

    public async Task WaitForInputAsync(
        InputIo input,
        bool value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_inputs[(int)input] == value)
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnInputChanged(InputIo changedInput, bool changedValue)
        {
            if (changedInput == input && changedValue == value)
            {
                completion.TrySetResult();
            }
        }

        InputChanged += OnInputChanged;
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        try
        {
            if (_inputs[(int)input] == value)
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

    public void SetInput(InputIo input, bool value)
    {
        var index = (int)input;
        if (_inputs[index] == value)
        {
            return;
        }

        _inputs[index] = value;
        InputChanged?.Invoke(input, value);
    }

    public void SetOutput(OutputIo output, bool value)
    {
        _outputs[(int)output] = value;
        OutputChanged?.Invoke(output, value);

        if (output == OutputIo.PcbSupplyUpstreamMachineReady)
        {
            SetInput(InputIo.PcbSupplyUpstreamBoardAvailable, value);
        }

        var feedbackVersion = Interlocked.Increment(
            ref _feedbackVersions[(int)output]);
        if (Hardware.OutputFeedbacks.TryGetValue(output, out var feedback))
        {
            _ = ApplyFeedbackAsync(
                output,
                value,
                feedback,
                feedbackVersion);
        }
    }

    public void TurnOffAll()
    {
        foreach (var output in Enum.GetValues<OutputIo>())
        {
            if (_outputs[(int)output])
            {
                SetOutput(output, false);
            }
        }
    }

    private void ReleaseCarrierJig(int stationIndex)
    {
        if (!_stationOccupied[stationIndex])
        {
            return;
        }

        var lastStation = Stations.Length - 1;
        if (stationIndex < lastStation
            && _stationOccupied[stationIndex + 1])
        {
            return;
        }

        if (stationIndex == lastStation
            && !_inputs[(int)InputIo.ConveyorDownstreamMachineReady])
        {
            return;
        }

        var current = Stations[stationIndex];
        var housing1Present = _inputs[(int)current.Housing1];
        var housing2Present = _inputs[(int)current.Housing2];
        ClearStation(stationIndex);

        if (stationIndex < lastStation)
        {
            SetStation(
                stationIndex + 1,
                housing1Present,
                housing2Present);
        }

        if (stationIndex == 0)
        {
            SetInput(InputIo.ConveyorUpstreamBoardAvailable, true);
        }
        else if (stationIndex == 1)
        {
            SetInput(InputIo.ConveyorDownstreamMachineReady, true);
        }
        else
        {
            SetInput(InputIo.ConveyorDownstreamMachineReady, false);
        }
    }

    private void SetStation(
        int stationIndex,
        bool housing1Present,
        bool housing2Present)
    {
        var station = Stations[stationIndex];
        _stationOccupied[stationIndex] = true;
        if (station.Carrier is { } carrier)
        {
            SetInput(carrier, true);
        }

        SetInput(station.Housing1, housing1Present);
        SetInput(station.Housing2, housing2Present);
    }

    private void ClearStation(int stationIndex)
    {
        var station = Stations[stationIndex];
        _stationOccupied[stationIndex] = false;
        if (station.Carrier is { } carrier)
        {
            SetInput(carrier, false);
        }

        SetInput(station.Housing1, false);
        SetInput(station.Housing2, false);
    }

    internal void StartConveyor()
    {
        _conveyorRunning = true;

        if (_outputs[(int)OutputIo.ConveyorUpstreamMachineReady]
            && _inputs[(int)InputIo.ConveyorUpstreamBoardAvailable]
            && !_stationOccupied[0])
        {
            SetInput(InputIo.ConveyorUpstreamBoardAvailable, false);
            SetStation(0, housing1Present: true, housing2Present: true);
        }
    }

    internal void StopConveyor() => _conveyorRunning = false;

    private async Task ApplyFeedbackAsync(
        OutputIo output,
        bool value,
        OutputFeedback feedback,
        int version)
    {
        await Task.Delay(FeedbackDelayMilliseconds);
        if (_feedbackVersions[(int)output] != version)
        {
            return;
        }

        var expected = feedback.GetExpected(value);
        if (feedback.OnInput != feedback.OffInput)
        {
            var inactiveInput = value
                ? feedback.OffInput
                : feedback.OnInput;
            var inactiveValue = value
                ? !feedback.OffValue
                : !feedback.OnValue;
            SetInput(inactiveInput, inactiveValue);
        }

        SetInput(expected.Input, expected.Value);
        ApplyPhysicalOutput(output, value);
    }

    private void ApplyPhysicalOutput(OutputIo output, bool value)
    {
        var stationIndex = Array.IndexOf(StopperUpOutputs, output);
        if (stationIndex >= 0 && value && _conveyorRunning)
        {
            ReleaseCarrierJig(stationIndex);
        }

        if (output == OutputIo.PcbSupplyGripperClose)
        {
            if (!value && _inputs[(int)InputIo.PcbSupplyPcbPresent])
            {
                SetInput(InputIo.PcbBufferPcbPresent, true);
            }

            SetInput(
                InputIo.PcbSupplyPcbPresent,
                value && SupplyPcbPresentOnPick);
        }

        if (output == OutputIo.PcbPlacementGripperClose)
        {
            SetInput(
                InputIo.PcbPlacementPcbPresent,
                value
                && PlacementPcbPresentOnPick
                && _inputs[(int)InputIo.PcbBufferPcbPresent]);
            if (value && _inputs[(int)InputIo.PcbPlacementPcbPresent])
            {
                SetInput(InputIo.PcbBufferPcbPresent, false);
            }
        }

        if (output == OutputIo.InspectionGripperClose
            && value
            && _outputs[(int)OutputIo.InspectionBackupPlateUp]
            && _stationOccupied[2])
        {
            ClearStation(2);
        }
    }
}
