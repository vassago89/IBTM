using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection.Training;

public enum BoltTrainingState
{
    [Description("Ready")]
    Ready,
    [Description("Loading Samples")]
    LoadingSamples,
    [Description("Saving")]
    Saving,
    [Description("Loading Model")]
    LoadingModel,
    [Description("Preparing Training Data")]
    Preparing,
    [Description("Training")]
    Training,
    [Description("Applying Model")]
    Applying,
    [Description("Completed")]
    Completed,
    [Description("Completed · Early Stop")]
    EarlyStopped,
    [Description("Review Model")]
    Reviewing,
    [Description("Cancelled")]
    Cancelled,
    [Description("Failed")]
    Failed,
}

public partial class BoltTrainingViewModel : ObservableObject, IProgress<BoltTrainingProgress>
{
    private readonly Func<BoltInspectionRecipe> _inspectionRecipe;
    private readonly BoltTrainingStore _store;
    private readonly TinyUnetTrainer _trainer = new();
    private readonly IBoltRecessSegmenter _segmenter;
    private readonly BoltTrainingSession _session;
    private bool _isActive;

    public BoltTrainingViewModel(
        Func<BoltInspectionRecipe> inspectionRecipe,
        BoltTrainingStore store,
        BoltTrainingSettings training,
        BoltImageCollector imageCollector,
        IBoltRecessSegmenter segmenter,
        BoltTrainingSession session)
    {
        _inspectionRecipe = inspectionRecipe;
        _store = store;
        _segmenter = segmenter;
        _session = session;
        Training = training;
        ImageCollector = imageCollector;
        Training.PropertyChanged += (_, e) =>
        {
            TrainCommand.NotifyCanExecuteChanged();
            SaveSettingsCommand.NotifyCanExecuteChanged();
            if (e.PropertyName == nameof(BoltTrainingSettings.MaskThreshold))
                Review?.Refresh();
        };
    }

    public BoltTrainingSettings Training { get; }
    public BoltImageCollector ImageCollector { get; }
    public InspectionImageCollection[] ImageCollectionModes { get; } = Enum.GetValues<InspectionImageCollection>();

    void IProgress<BoltTrainingProgress>.Report(BoltTrainingProgress progress)
    {
        Epoch = progress.Epoch;
        BestEpoch = progress.BestEpoch;
        TrainingLoss = progress.TrainingLoss;
        ValidationLoss = progress.ValidationLoss;
    }

    public int CollectedImageCount
    {
        get
        {
            return Samples.Count(sample => sample.Inspection is not null);
        }
    }

    [ObservableProperty]
    private double _databaseMegabytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollectedImageCount))]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    private IReadOnlyList<BoltSampleInfo> _samples = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleIncludedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSampleUseCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(InspectSampleCommand))]
    private BoltSampleInfo? _currentSample;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSamplesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleIncludedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSampleUseCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewSavedModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(InspectSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    [NotifyPropertyChangedFor(nameof(ControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(CanEditRegion))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaximumRegionSize))]
    [NotifyPropertyChangedFor(nameof(CanEditRegion))]
    private BitmapSource? _originalImage;
    private int _regionSize = IBoltRecessSegmenter.InputSize;
    [ObservableProperty]
    private Point[] _labelPolygon = [];
    [ObservableProperty]
    private BoltModelReview? _review;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private BoltTrainingState _state;
    [ObservableProperty]
    private int _epoch;
    [ObservableProperty]
    private int _bestEpoch;
    [ObservableProperty]
    private double _trainingLoss;
    [ObservableProperty]
    private double _validationLoss;

    [ObservableProperty]
    private string? _error;

    public bool ControlsEnabled
    {
        get
        {
            return !IsBusy;
        }
    }

    public bool CanEditRegion
    {
        get
        {
            return ControlsEnabled && OriginalImage is not null;
        }
    }

    public int MaximumRegionSize
    {
        get
        {
            return OriginalImage is { } image ? Math.Min(image.PixelWidth, image.PixelHeight) : 1;
        }
    }

    public int RegionSize
    {
        get
        {
            return _regionSize;
        }

        set
        {
            if (SetProperty(ref _regionSize, Math.Clamp(value, 1, MaximumRegionSize)))
                ClearSampleReview();
        }
    }

    public void Activate()
    {
        _isActive = true;
        Review?.Refresh();
        _session.SetRunning(IsBusy || CurrentSample is not null);
        if (!IsBusy)
            LoadSamplesCommand.Execute(null);
    }

    public void Deactivate()
    {
        _isActive = false;
        foreach (var command in Commands())
            command.Cancel();
        _session.SetRunning(IsBusy);
    }

    public async Task ShutdownAsync()
    {
        var pending = Commands().Select(command => command.ExecutionTask).OfType<Task>().ToArray();
        Deactivate();
        await Task.WhenAll(pending);
    }

    private IAsyncRelayCommand[] Commands()
    {
        return [
            LoadSamplesCommand,
            OpenSampleCommand,
            CompleteLabelCommand,
            EmptyLabelCommand,
            ToggleIncludedCommand,
            ToggleSampleUseCommand,
            PreviousSampleCommand,
            NextSampleCommand,
            TrainCommand,
            ReviewSavedModelCommand,
            InspectSampleCommand,
            SaveSettingsCommand
        ];
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private Task SaveSettingsAsync(CancellationToken token)
    {
        return RunAsync(
            BoltTrainingState.Saving,
            async ct =>
            {
                await Task.Run(() => _store.SaveSettings(Training), ct);
                ImageCollector.ClearError();
            },
            token);
    }

    private bool CanSaveSettings()
    {
        return ControlsEnabled && Training.IsValid;
    }

    [RelayCommand(CanExecute = nameof(ControlsEnabled))]
    private Task LoadSamplesAsync(CancellationToken token)
    {
        return RunAsync(
            BoltTrainingState.LoadingSamples,
            ct => ReloadSamplesAsync(CurrentSample?.Id, ct, clearSelection: CurrentSample is null),
            token);
    }

    [RelayCommand(CanExecute = nameof(ControlsEnabled))]
    private Task OpenSampleAsync(BoltSampleInfo sample, CancellationToken token)
    {
        return RunAsync(BoltTrainingState.LoadingSamples, ct => ShowSampleAsync(sample, ct), token);
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousSampleAsync(CancellationToken token)
    {
        return OpenSampleAsync(Samples.Last(sample => sample.Id < CurrentSample!.Id), token);
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextSampleAsync(CancellationToken token)
    {
        return OpenSampleAsync(Samples.First(sample => sample.Id > CurrentSample!.Id), token);
    }

    [RelayCommand(CanExecute = nameof(CanCompleteLabel))]
    private Task CompleteLabelAsync(Point[] polygon, CancellationToken token)
    {
        return SaveLabelAsync(BoltLabel.Bolt, polygon, token);
    }

    [RelayCommand(CanExecute = nameof(CanLabel))]
    private Task EmptyLabelAsync(CancellationToken token)
    {
        return SaveLabelAsync(BoltLabel.Empty, [], token);
    }

    private Task SaveLabelAsync(BoltLabel label, Point[] polygon, CancellationToken token)
    {
        return RunAsync(
            BoltTrainingState.Saving,
            async ct =>
            {
                var id = CurrentSample!.Id;
                await Task.Run(() => _store.SaveLabel(id, label, polygon, RegionSize), ct);
                Review = null;
                var next = Samples.FirstOrDefault(sample => sample.Id > id)?.Id;
                await ReloadSamplesAsync(next, ct, clearSelection: next is null);
            },
            token);
    }

    [RelayCommand(CanExecute = nameof(CanLabel))]
    private Task ToggleIncludedAsync(CancellationToken token)
    {
        return RunAsync(
            BoltTrainingState.Saving,
            async ct =>
            {
                var sample = CurrentSample!;
                await Task.Run(() => _store.SetIncluded(sample.Id, !sample.Included), ct);
                Review = null;
                await ReloadSamplesAsync(sample.Id, ct);
            },
            token);
    }

    [RelayCommand(CanExecute = nameof(CanChangeUse))]
    private Task ToggleSampleUseAsync(CancellationToken token)
    {
        return RunAsync(
            BoltTrainingState.Saving,
            async ct =>
            {
                var sample = CurrentSample!;
                await Task.Run(
                    () => _store.SetUse(
                        sample.Id,
                        sample.Use == BoltSampleUse.Training
                            ? BoltSampleUse.Validation
                            : BoltSampleUse.Training),
                    ct);
                Review = null;
                await ReloadSamplesAsync(sample.Id, ct);
            },
            token);
    }

    [RelayCommand(CanExecute = nameof(CanTrain))]
    private Task TrainAsync(CancellationToken token)
    {
        Review = null;
        Epoch = 0;
        BestEpoch = 0;
        TrainingLoss = 0;
        ValidationLoss = 0;
        return RunAsync(
            BoltTrainingState.Preparing,
            async ct =>
            {
                await Task.Run(() => _store.SaveSettings(Training), ct);
                var dataset = await Task.Run(() => new BoltSegmentationDataset(_store, ct), ct);
                ct.ThrowIfCancellationRequested();
                State = BoltTrainingState.Training;
                var trained = await Task.Run(() => _trainer.Train(dataset, Training, this, ct), ct);
                var review = await Task.Run(
                    () => BoltModelReview.Load(
                        trained.Weights,
                        dataset.Validation,
                        _inspectionRecipe,
                        Training,
                        ct),
                    ct);
                ct.ThrowIfCancellationRequested();
                State = BoltTrainingState.Applying;
                await Task.Run(
                    () =>
                    {
                        _store.SaveModel(trained);
                        _segmenter.Reload();
                    });
                Review = review;
                State = trained.Epochs < Training.MaxEpochs
                    ? BoltTrainingState.EarlyStopped
                    : BoltTrainingState.Completed;
            },
            token);
    }

    [RelayCommand(CanExecute = nameof(ControlsEnabled))]
    private Task ReviewSavedModelAsync(CancellationToken token)
    {
        return RunAsync(
            BoltTrainingState.LoadingModel,
            async ct =>
            {
                Review = null;
                var review = await Task.Run(
                    () => BoltModelReview.Load(
                        _store.LoadModel().Weights,
                        BoltSegmentationDataset.LoadValidation(_store, ct),
                        _inspectionRecipe,
                        Training,
                        ct),
                    ct);
                ct.ThrowIfCancellationRequested();
                Review = review;
                State = BoltTrainingState.Reviewing;
            },
            token);
    }

    [RelayCommand(CanExecute = nameof(CanLabel))]
    private Task InspectSampleAsync(CancellationToken token)
    {
        return RunAsync(
            BoltTrainingState.LoadingModel,
            async ct =>
            {
                Review = null;
                var sample = CurrentSample!;
                var original = OriginalImage!;
                var regionSize = RegionSize;
                var review = await Task.Run(
                    () => BoltModelReview.LoadImage(
                        _store.LoadModel().Weights,
                        sample,
                        BoltTrainingImages.ToFrame(original),
                        regionSize,
                        _inspectionRecipe,
                        Training,
                        ct),
                    ct);
                ct.ThrowIfCancellationRequested();
                Review = review;
                State = BoltTrainingState.Reviewing;
            },
            token);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        foreach (var command in Commands())
            command.Cancel();
        if (!IsBusy)
        {
            ClearSample();
            Error = null;
            State = BoltTrainingState.Cancelled;
            _session.SetRunning(false);
        }
    }

    private async Task ReloadSamplesAsync(long? id, CancellationToken token, bool clearSelection = false)
    {
        var (samples, bytes) = await Task.Run(
            () => (_store.GetSamples(), _store.GetDatabaseSizeBytes()),
            token);
        token.ThrowIfCancellationRequested();
        Samples = samples;
        DatabaseMegabytes = bytes / (1024d * 1024);
        var sample = clearSelection
            ? null
            : samples.FirstOrDefault(sample => sample.Id == id) ?? samples.FirstOrDefault();
        if (sample is null)
            ClearSample();
        else
            await ShowSampleAsync(sample, token);
    }

    private async Task ShowSampleAsync(BoltSampleInfo sample, CancellationToken token)
    {
        ClearSampleReview();
        var original = await Task.Run(
            () => BoltTrainingImages.Create(_store.LoadImage(sample.Id)),
            token);
        token.ThrowIfCancellationRequested();
        OriginalImage = original;
        RegionSize = sample.RegionSize;
        LabelPolygon = [.. sample.Polygon];
        CurrentSample = sample;
    }

    private void ClearSample()
    {
        ClearSampleReview();
        CurrentSample = null;
        OriginalImage = null;
        LabelPolygon = [];
    }

    private void ClearSampleReview()
    {
        if (Review?.Scope == BoltReviewScope.SelectedImage)
            Review = null;
    }

    private async Task RunAsync(
        BoltTrainingState state,
        Func<CancellationToken, Task> action,
        CancellationToken token)
    {
        IsBusy = true;
        State = state;
        Error = null;
        _session.SetRunning(true);
        try
        {
            await action(token);
            if (State == state)
                State = BoltTrainingState.Ready;
        }
        catch (OperationCanceledException)
        {
            State = BoltTrainingState.Cancelled;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            State = BoltTrainingState.Failed;
        }
        finally
        {
            IsBusy = false;
            _session.SetRunning(_isActive && CurrentSample is not null);
        }
    }

    private bool CanLabel()
    {
        return ControlsEnabled && CurrentSample is not null;
    }

    private bool CanChangeUse()
    {
        return CanLabel() && CurrentSample!.Label != BoltLabel.Unlabeled;
    }

    private bool CanCompleteLabel(Point[]? points)
    {
        return CanLabel() && points is { Length: >= 3 };
    }

    private bool CanTrain()
    {
        return ControlsEnabled
            && Training.IsValid
            && Samples.Any(sample => sample.Included && sample.Label != BoltLabel.Unlabeled);
    }

    private bool CanPrevious()
    {
        return CanLabel() && Samples.Any(sample => sample.Id < CurrentSample!.Id);
    }

    private bool CanNext()
    {
        return CanLabel() && Samples.Any(sample => sample.Id > CurrentSample!.Id);
    }

    private bool CanCancel()
    {
        return IsBusy ? State != BoltTrainingState.Applying : CurrentSample is not null;
    }

}
