using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualIoService(HardwareMap? hardware = null) : IIoService
{
    private static readonly (InputIo Carrier, InputIo Housing1, InputIo Housing2)[] Stations =
    [
        (
            InputIo.PcbPlacementCarrierJigPresent,
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

    private readonly Dictionary<InputIo, bool> _inputs =
        Enum.GetValues<InputIo>().ToDictionary(input => input, _ => false);
    private readonly Dictionary<OutputIo, bool> _outputs =
        Enum.GetValues<OutputIo>().ToDictionary(output => output, _ => false);
    private bool _conveyorRunning;

    private event Action<InputIo, bool>? InputChanged;

    public HardwareMap Hardware { get; } = hardware ?? new HardwareMap();
    public HashSet<OutputIo> DisabledFeedbacks { get; } = [];
    public bool SupplyPcbPresentOnPick { get; set; } = true;
    public bool PlacementPcbPresentOnPick { get; set; } = true;

    public void Initialize()
    {
        SetInput(InputIo.MainLaneUpstreamBoardAvailable, true);
        SetInput(InputIo.MainLaneDownstreamMachineReady, true);
        SetInput(InputIo.EmergencyStopReleased, true);
        SetInput(InputIo.DoorClosed, true);
        SetInput(InputIo.AirPressureOk, true);
        SetInput(InputIo.PcbSupplyRotationHome, true);
    }

    public bool GetInput(InputIo input) => _inputs[input];

    public bool GetOutput(OutputIo output) => _outputs[output];

    public async Task WaitForInputAsync(
        InputIo input,
        bool value,
        CancellationToken cancellationToken = default)
    {
        if (_inputs[input] == value)
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
            if (_inputs[input] == value)
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
        _inputs[input] = value;
        InputChanged?.Invoke(input, value);
    }

    public void SetOutput(OutputIo output, bool value)
    {
        _outputs[output] = value;

        var stationIndex = Array.IndexOf(StopperUpOutputs, output);
        if (stationIndex >= 0 && value && _conveyorRunning)
        {
            ReleaseCarrierJig(stationIndex);
        }

        if (output == OutputIo.PcbSupplyReady)
        {
            SetInput(InputIo.PcbSupplyCarrierAvailable, value);
        }

        if (output == OutputIo.PcbSupplyGripper)
        {
            SetInput(
                InputIo.PcbSupplyPcbPresent,
                value && SupplyPcbPresentOnPick);
        }

        if (output == OutputIo.PcbPlacementGripper)
        {
            SetInput(
                InputIo.PcbPlacementPcbPresent,
                value
                && PlacementPcbPresentOnPick
                && _inputs[InputIo.PcbSupplyPcbPresent]);
        }

        if (output == OutputIo.InspectionGripper
            && value
            && _outputs[OutputIo.InspectionBackupPlateUp]
            && _inputs[InputIo.InspectionCarrierJigPresent])
        {
            ClearStation(2);
        }

        if (Hardware.OutputFeedbacks.TryGetValue(output, out var feedback)
            && !DisabledFeedbacks.Contains(output))
        {
            if (feedback.OnInput == feedback.OffInput)
            {
                var expected = feedback.GetExpected(value);
                SetInput(expected.Input, expected.Value);
            }
            else if (value)
            {
                SetInput(feedback.OffInput, !feedback.OffValue);
                SetInput(feedback.OnInput, feedback.OnValue);
            }
            else
            {
                SetInput(feedback.OnInput, !feedback.OnValue);
                SetInput(feedback.OffInput, feedback.OffValue);
            }
        }
    }

    public void TurnOffAll()
    {
        foreach (var output in Enum.GetValues<OutputIo>())
        {
            if (_outputs[output])
            {
                SetOutput(output, false);
            }
        }
    }

    private void ReleaseCarrierJig(int stationIndex)
    {
        var current = Stations[stationIndex];
        if (!_inputs[current.Carrier])
        {
            throw new InvalidOperationException($"Station {stationIndex + 1} has no carrier jig.");
        }

        if (stationIndex < Stations.Length - 1)
        {
            var destination = Stations[stationIndex + 1];
            if (_inputs[destination.Carrier])
            {
                throw new InvalidOperationException($"Station {stationIndex + 2} is occupied.");
            }
        }

        var housing1Present = _inputs[current.Housing1];
        var housing2Present = _inputs[current.Housing2];
        ClearStation(stationIndex);

        if (stationIndex < Stations.Length - 1)
        {
            SetStation(
                stationIndex + 1,
                housing1Present,
                housing2Present);
        }

        if (stationIndex == 0)
        {
            SetInput(InputIo.MainLaneUpstreamBoardAvailable, true);
        }
        else if (stationIndex == 1)
        {
            SetInput(InputIo.MainLaneDownstreamMachineReady, true);
        }
        else
        {
            SetInput(InputIo.MainLaneDownstreamMachineReady, false);
        }
    }

    private void SetStation(
        int stationIndex,
        bool housing1Present,
        bool housing2Present)
    {
        var station = Stations[stationIndex];
        SetInput(station.Carrier, true);
        SetInput(station.Housing1, housing1Present);
        SetInput(station.Housing2, housing2Present);
    }

    private void ClearStation(int stationIndex)
    {
        var station = Stations[stationIndex];
        SetInput(station.Carrier, false);
        SetInput(station.Housing1, false);
        SetInput(station.Housing2, false);
    }

    internal void StartConveyor()
    {
        _conveyorRunning = true;

        if (_outputs[OutputIo.MainLaneUpstreamMachineReady]
            && _inputs[InputIo.MainLaneUpstreamBoardAvailable]
            && !_inputs[InputIo.PcbPlacementCarrierJigPresent])
        {
            SetInput(InputIo.MainLaneUpstreamBoardAvailable, false);
            SetStation(0, housing1Present: true, housing2Present: true);
        }
    }

    internal void StopConveyor() => _conveyorRunning = false;
}
