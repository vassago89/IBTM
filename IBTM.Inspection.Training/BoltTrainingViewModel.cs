using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using Microsoft.Win32;

namespace IBTM.Inspection.Training;

public enum BoltTrainingState
{
    [Description("Ready")]
    Ready,

    [Description("Capturing Bolt Points")]
    Capturing,

    [Description("Loading Data")]
    LoadingData,

    [Description("Training")]
    Training,

    [Description("Review Model")]
    Reviewing,

    [Description("Cancelled")]
    Cancelled,

    [Description("Failed")]
    Failed,
}

public partial class BoltTrainingViewModel : ObservableObject
{
    private static readonly string DefaultDatasetDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "TrainingData",
        "BoltRecess");

    private readonly BoltInspectionSettings _settings;
    private readonly TinyUnetTrainer _trainer;
    private readonly IBoltRecessSegmenter _segmenter;
    private readonly BoltTrainingSession _session;
    private readonly BoltImageCapture _imageCapture;
    private readonly InspectionWork _inspectionWork;
    private readonly Func<IReadOnlyList<BoltPoint>> _boltPoints;
    private IReadOnlyList<BoltImage> _labelImages = [];
    private int _labelIndex;

    public BoltTrainingViewModel(
        BoltInspectionSettings settings,
        TinyUnetTrainer trainer,
        IBoltRecessSegmenter segmenter,
        BoltTrainingSession session,
        BoltImageCapture imageCapture,
        InspectionWork inspectionWork,
        Func<IReadOnlyList<BoltPoint>> boltPoints)
    {
        _settings = settings;
        _trainer = trainer;
        _segmenter = segmenter;
        _session = session;
        _imageCapture = imageCapture;
        _inspectionWork = inspectionWork;
        _boltPoints = boltPoints;
        inspectionWork.Changed += OnInspectionWorkChanged;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureBoltPointsCommand))]
    private string _datasetDirectory = DefaultDatasetDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    private int _epochs = 50;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureBoltPointsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyLabelCommand))]
    [NotifyPropertyChangedFor(nameof(InputsEnabled))]
    [NotifyPropertyChangedFor(nameof(ConfigurationEnabled))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureBoltPointsCommand))]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectDatasetCommand))]
    [NotifyPropertyChangedFor(nameof(HasLabelImage))]
    [NotifyPropertyChangedFor(nameof(ConfigurationEnabled))]
    private BitmapSource? _labelImage;

    [ObservableProperty]
    private string? _labelImageName;

    [ObservableProperty]
    private int _labelImageNumber;

    [ObservableProperty]
    private int _selectedImageCount;

    [ObservableProperty]
    private BoltModelReview? _review;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private BoltTrainingState _state;

    [ObservableProperty]
    private int _epoch;

    [ObservableProperty]
    private double _trainingLoss;

    [ObservableProperty]
    private double _validationLoss;

    [ObservableProperty]
    private string? _error;

    public bool InputsEnabled => !IsBusy;
    public bool HasLabelImage => LabelImage is not null;
    public bool ConfigurationEnabled => InputsEnabled && !HasLabelImage;

    public void Activate() =>
        _session.SetRunning(IsBusy || HasLabelImage);

    public void Deactivate()
    {
        CaptureBoltPointsCommand.Cancel();
        TrainCommand.Cancel();
        if (!IsBusy)
        {
            _session.SetRunning(false);
        }
    }

    [RelayCommand(CanExecute = nameof(ConfigurationEnabled))]
    private void SelectDataset()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Bolt Inspection Dataset",
            InitialDirectory = DatasetDirectory,
        };
        if (dialog.ShowDialog() == true)
        {
            DatasetDirectory = dialog.FolderName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureBoltPointsAsync(
        CancellationToken cancellationToken)
    {
        Begin(BoltTrainingState.Capturing);
        try
        {
            var points = _boltPoints()
                .Where(point =>
                    _inspectionWork.HousingPresent(point.Housing))
                .ToArray();
            _labelImages = await _imageCapture.CaptureAsync(
                points,
                cancellationToken);
            _labelIndex = 0;
            SelectedImageCount = _labelImages.Count;
            ShowLabelImage();
            State = BoltTrainingState.Ready;
        }
        catch (OperationCanceledException)
        {
            State = BoltTrainingState.Cancelled;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            End();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLabel))]
    private void CompleteLabel(byte[] mask) => SaveLabel(mask);

    [RelayCommand(CanExecute = nameof(CanLabel))]
    private void EmptyLabel() => SaveLabel(
        new byte[IBoltRecessSegmenter.InputSize
                 * IBoltRecessSegmenter.InputSize]);

    [RelayCommand(CanExecute = nameof(CanTrain))]
    private async Task TrainAsync(CancellationToken cancellationToken)
    {
        var modelFile = ModelFilePath();
        Review = null;
        Begin(BoltTrainingState.LoadingData);
        Epoch = 0;
        TrainingLoss = 0;
        ValidationLoss = 0;

        try
        {
            var dataset = await Task.Run(
                () => new BoltSegmentationDataset(
                    DatasetDirectory,
                    IBoltRecessSegmenter.InputSize,
                    cancellationToken),
                cancellationToken);
            State = BoltTrainingState.Training;

            var progress = new Progress<BoltTrainingProgress>(value =>
            {
                Epoch = value.Epoch;
                TrainingLoss = value.TrainingLoss;
                ValidationLoss = value.ValidationLoss;
            });
            await Task.Run(
                () => _trainer.Train(
                    dataset,
                    modelFile,
                    Epochs,
                    progress,
                    cancellationToken),
                cancellationToken);

            _segmenter.Reload();
            Review = await Task.Run(
                () => BoltModelReview.Load(
                    modelFile,
                    dataset.Validation,
                    _settings,
                    cancellationToken),
                cancellationToken);
            await _settings.SaveAsync(cancellationToken);
            State = BoltTrainingState.Reviewing;
        }
        catch (OperationCanceledException)
        {
            State = BoltTrainingState.Cancelled;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            End();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        CaptureBoltPointsCommand.Cancel();
        TrainCommand.Cancel();
    }

    private bool CanCapture() =>
        ConfigurationEnabled
        && _inspectionWork.Ready
        && _boltPoints().Any(point =>
            _inspectionWork.HousingPresent(point.Housing));

    private bool CanTrain() =>
        ConfigurationEnabled
        && Epochs > 0
        && !string.IsNullOrWhiteSpace(DatasetDirectory);

    private bool CanLabel() => InputsEnabled && HasLabelImage;
    private bool CanCancel() => IsBusy
                                && State is BoltTrainingState.Capturing
                                    or BoltTrainingState.LoadingData
                                    or BoltTrainingState.Training;
    private void SaveLabel(byte[] mask)
    {
        Review = null;
        BoltTrainingFiles.SaveSample(
            DatasetDirectory,
            LabelImage!,
            mask);
        _labelIndex++;
        ShowLabelImage();
        _session.SetRunning(HasLabelImage);
    }

    private void ShowLabelImage()
    {
        if (_labelIndex >= _labelImages.Count)
        {
            LabelImage = null;
            LabelImageName = null;
            LabelImageNumber = 0;
            SelectedImageCount = 0;
            return;
        }

        var capture = _labelImages[_labelIndex];
        LabelImage = BoltTrainingFiles.CreateInput(capture.Image);
        LabelImageName =
            $"{capture.Point.Housing.GetDescription()} · Bolt {capture.Point.Number}";
        LabelImageNumber = _labelIndex + 1;
    }

    private void Begin(BoltTrainingState state)
    {
        IsBusy = true;
        State = state;
        Error = null;
        _session.SetRunning(true);
    }

    private void End()
    {
        IsBusy = false;
        _session.SetRunning(HasLabelImage);
    }

    private void Fail(Exception exception)
    {
        Error = exception.Message;
        State = BoltTrainingState.Failed;
    }

    private void OnInspectionWorkChanged() =>
        Application.Current.Dispatcher.BeginInvoke(
            CaptureBoltPointsCommand.NotifyCanExecuteChanged);

    private string ModelFilePath() => Path.Combine(
        AppContext.BaseDirectory,
        _settings.ModelFile);

    partial void OnDatasetDirectoryChanged(string value) => Review = null;
}
