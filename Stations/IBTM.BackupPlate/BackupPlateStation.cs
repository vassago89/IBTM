using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.BackupPlate;

public sealed class BackupPlateStation : INotifyPropertyChanged
{
    private readonly IIoService _io;
    private readonly InputIo? _backupPlateUp;
    private readonly InputIo? _backupPlateDown;
    private readonly InputIo _stopperUp;
    private readonly InputIo _stopperDown;
    private readonly InputIo _heatSink1;
    private readonly OutputIo? _backupPlate;
    private readonly BackupPlateSettings? _settings;
    private readonly IAxisMotion? _motion;
    private readonly OutputIo _stopper;
    // Protect only result ownership changes, never device calls or notifications.
    private static readonly Lock s_jobGate;
    private volatile Job _job;

    public BackupPlateStation(
        IIoService io,
        [FromKeyedServices(MotionGroup.BoltFasteningBackup)] IAxisMotion motion,
        [FromKeyedServices(MotionGroup.BoltFasteningBackup)] MotionStatus status,
        BackupPlateSettings settings)
        : this(
            io,
            null,
            null,
            InputIo.BoltFasteningStopperUp,
            InputIo.BoltFasteningStopperDown,
            InputIo.BoltFasteningHeatSink1Present,
            InputIo.BoltFasteningHeatSink2Present,
            null,
            OutputIo.BoltFasteningStopperUp,
            status,
            settings)
    {
        _motion = motion;
    }

    private BackupPlateStation(
        IIoService io,
        InputIo? backupPlateUp,
        InputIo? backupPlateDown,
        InputIo stopperUp,
        InputIo stopperDown,
        InputIo heatSink1,
        InputIo heatSink2,
        OutputIo? backupPlate,
        OutputIo stopper,
        MotionStatus? motion = null,
        BackupPlateSettings? settings = null)
    {
        _job = new();
        _io = io;
        _backupPlateUp = backupPlateUp;
        _backupPlateDown = backupPlateDown;
        _stopperUp = stopperUp;
        _stopperDown = stopperDown;
        _heatSink1 = heatSink1;
        HeatSink2Input = heatSink2;
        _backupPlate = backupPlate;
        _stopper = stopper;
        Motion = motion;
        _settings = settings;
        if (motion is not null)
        {
            motion.PropertyChanged += OnMotionChanged;
            motion.Axes[MotionAxis.Z].PropertyChanged += OnMotionChanged;
        }
        io.InputChanged += OnInputChanged;
    }

    static BackupPlateStation()
    {
        s_jobGate = new();
    }

    public InputIo HeatSink2Input { get; }
    public MotionStatus? Motion { get; }

    public event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CarrierPresent => _io.GetInput(_heatSink1) || _io.GetInput(HeatSink2Input);

    public StationCylinderState BackupPlate
    {
        get
        {
            if (Motion is { } motion)
            {
                if (_settings!.ValidationError is not null
                    || motion.Axes[MotionAxis.Z].State is not
                        { Homed: true, ServoOn: true, Alarm: false, Emergency: false, InMotion: false }
                    || motion.Position.Z is not { } position || !double.IsFinite(position))
                    return StationCylinderState.Between;
                if (Math.Abs(position - _settings.FasteningHeight!.Value) <= _settings.PositionTolerance)
                    return StationCylinderState.Up;
                if (Math.Abs(position - _settings.TransferHeight!.Value) <= _settings.PositionTolerance)
                    return StationCylinderState.Down;
                return StationCylinderState.Between;
            }
            switch ((_io.GetInput(_backupPlateUp!.Value), _io.GetInput(_backupPlateDown!.Value)))
            {
                case (true, false):
                    return StationCylinderState.Up;
                case (false, true):
                    return StationCylinderState.Down;
                default:
                    return StationCylinderState.Between;
            }
        }
    }

    public StationCylinderState Stopper
    {
        get
        {
            switch ((_io.GetInput(_stopperUp), _io.GetInput(_stopperDown)))
            {
                case (true, false):
                    return StationCylinderState.Up;
                case (false, true):
                    return StationCylinderState.Down;
                default:
                    return StationCylinderState.Between;
            }
        }
    }

    public bool CarrierSeated
    {
        get
        {
            return CarrierPresent
                && BackupPlate == StationCylinderState.Up;
        }
    }

    public static BackupPlateStation CreatePcbPlacement(IIoService io)
    {
        return new(
            io,
            InputIo.PcbPlacementBackupPlateUp,
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementStopperDown,
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementHeatSink2Present,
            OutputIo.PcbPlacementBackupPlateUp,
            OutputIo.PcbPlacementStopperUp);
    }

    public static BackupPlateStation CreateInspection(IIoService io)
    {
        return new(
            io,
            InputIo.InspectionBackupPlateUp,
            InputIo.InspectionBackupPlateDown,
            InputIo.InspectionStopperUp,
            InputIo.InspectionStopperDown,
            InputIo.InspectionHeatSink1Present,
            InputIo.InspectionHeatSink2Present,
            OutputIo.InspectionBackupPlateUp,
            OutputIo.InspectionStopperUp);
    }

    public bool IsHeatSinkPresent(HeatSinkSlot heatSink)
    {
        return _io.GetInput(heatSink == HeatSinkSlot.HeatSink1 ? _heatSink1 : HeatSink2Input);
    }

    public Task PrepareToReceiveAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            SetStopperUpAsync(true, cancellationToken),
            SetBackupPlateUpAsync(false, cancellationToken));
    }

    public Task ReleaseAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            SetStopperUpAsync(false, cancellationToken),
            SetBackupPlateUpAsync(false, cancellationToken));
    }

    public async Task SeatAsync(CancellationToken cancellationToken)
    {
        await SetStopperUpAsync(true, cancellationToken);
        await SetBackupPlateUpAsync(true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CarrierPresent)
            throw new MotionInterlockException(UiText.Format(
                $"{UiText.Get("Backup")}: carrier not detected after raising the backup plate."));
        await SetStopperUpAsync(false, cancellationToken);
    }

    public async Task SetBackupPlateUpAsync(bool up, CancellationToken cancellationToken)
    {
        if (_motion is not { } motion)
        {
            await _io.SetOutputAndWaitAsync(_backupPlate!.Value, up, cancellationToken);
            return;
        }
        if (_settings!.ValidationError is { } error)
            throw new InvalidOperationException(error);
        if (!MotionServiceBase.IsReadyAndStopped(motion))
            throw new MotionInterlockException(UiText.Get("S2 backup motion is not ready"));
        var target = up ? _settings.FasteningHeight!.Value : _settings.TransferHeight!.Value;
        await motion.MoveAxisAsync(MotionAxis.Z, target, _settings.Motion.ZSpeed, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var status = Motion!;
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        if (BackupPlate != (up ? StationCylinderState.Up : StationCylinderState.Down))
            throw new MotionInterlockException(UiText.Get("S2 backup did not reach the target height"));
    }

    private void OnMotionChanged(object? sender, PropertyChangedEventArgs args)
    {
        Changed?.Invoke();
    }

    public Task SetStopperUpAsync(bool up, CancellationToken cancellationToken)
    {
        return _io.SetOutputAndWaitAsync(_stopper, up, cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == _backupPlateUp
            || input == _backupPlateDown
            || input == _stopperUp
            || input == _stopperDown
            || input == _heatSink1
            || input == HeatSink2Input)
        {
            Changed?.Invoke();
        }
    }

    public event Action<HeatSinkAssembly>? AssemblyCreated;

    public Job CurrentJob => _job;

    public bool Completed => CarrierPresent && _job.Completed;

    public double? LastCycleSeconds { get; private set; }

    public bool IsRestartAllowed => CarrierPresent && _job.RestartAllowed;

    public IEnumerable<HeatSinkAssembly> Assemblies => _job.Assemblies.Values;

    public HeatSinkAssembly GetAssembly(HeatSinkSlot heatSink)
    {
        return GetAssembly(CurrentJob, heatSink);
    }

    public HeatSinkAssembly GetAssembly(Job job, HeatSinkSlot heatSink)
    {
        HeatSinkAssembly assembly;
        lock (s_jobGate)
        {
            RequireCurrentJob(job);
            if (job.Assemblies.TryGetValue(heatSink, out var existing))
                return existing;
            assembly = new(heatSink);
            job.Assemblies[heatSink] = assembly;
        }
        AssemblyCreated?.Invoke(assembly);
        return assembly;
    }

    public void ClearJob()
    {
        lock (s_jobGate)
        {
            if (CarrierPresent)
                throw new InvalidOperationException("Remove the carrier before clearing its work.");
            _job = new();
        }
        Changed?.Invoke();
    }

    public void RequireCurrentJob(Job job)
    {
        if (!ReferenceEquals(_job, job))
            throw new InvalidOperationException(
                $"Carrier work changed from {job.Id} to {_job.Id}; the previous work cannot update this carrier.");
    }

    public void TransferAssembliesTo(BackupPlateStation destination, Job job, Job arrivingJob)
    {
        lock (s_jobGate)
        {
            // Arrival ownership must still match when the result is committed.
            // A later receipt must not inherit results from an earlier transfer.
            destination.RequireCurrentJob(arrivingJob);
            // The carrier keeps its trace number; each station gets a new completion owner.
            var received = new Job(job.Id);
            foreach (var assembly in job.Assemblies.Values)
                received.Assemblies[assembly.HeatSink] = assembly;
            destination._job = received;
            // A new carrier may already occupy the source. Only release the load
            // captured when this physical transfer started, never the new load.
            if (ReferenceEquals(_job, job))
                _job = new();
        }

        destination.Changed?.Invoke();
        Changed?.Invoke();
    }

    public void Complete(Job? job = null, TimeSpan? cycleTime = null)
    {
        lock (s_jobGate)
        {
            // Disabled units complete the current carrier atomically with result handoff.
            job ??= _job;
            RequireCurrentJob(job);
            if (job.Completed)
                return;
            if (cycleTime is { } duration)
                LastCycleSeconds = duration.TotalSeconds;
            job.Completed = true;
            job.RestartAllowed = false;
        }
        if (cycleTime is not null)
            PropertyChanged?.Invoke(this, new(nameof(LastCycleSeconds)));
        Changed?.Invoke();
    }

    public void Restart(Job job, bool allowStart = false)
    {
        lock (s_jobGate)
        {
            RequireCurrentJob(job);
            job.Completed = false;
            // Only an explicit operator clear admits unfinished work at the next START.
            job.RestartAllowed = allowStart;
        }
        // Keep recorded results with the carrier; the caller decides whether to clear or resume them.
        Changed?.Invoke();
    }

    public sealed class Job
    {
        private static long s_nextId;
        internal readonly ConcurrentDictionary<HeatSinkSlot, HeatSinkAssembly> Assemblies;
        internal volatile bool Completed;
        internal volatile bool RestartAllowed;

        internal Job(long? id = null)
        {
            Assemblies = new();

            Id = id ?? Interlocked.Increment(ref s_nextId);
        }

        public long Id { get; }
    }
}
