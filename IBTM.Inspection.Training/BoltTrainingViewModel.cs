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
using IBTM.Device;
using Microsoft.Win32;

namespace IBTM.Inspection.Training;

public enum BoltTrainingState
{
    [Description("Ready")]
    Ready,

    [Description("Capturing Bolt Points")]
    Capturing,

    [Description("Loading Images")]
    LoadingImages,

    [Description("Loading Data")]
    LoadingData,

    [Description("Training")]
    Training,

    [Description("Applying Model")]
    Applying,

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
    private readonly TinyUnetTrainer _trainer = new();
    private readonly IBoltRecessSegmenter _segmenter;
    private readonly BoltTrainingSession _session;
    private readonly OperationCancellation _operations;
    private readonly BoltInspector _inspector;
    private readonly InspectionWork _inspectionWork;
    private readonly Func<bool> _captureEnabled;
    private readonly Func<IReadOnlyList<BoltPoint>> _boltPoints;
    private IReadOnlyList<LabelCapture> _labelImages = [];
    private int _labelIndex;

    public BoltTrainingViewModel(
        BoltInspectionSettings settings,
        IBoltRecessSegmenter segmenter,
        BoltTrainingSession session,
        OperationCancellation operations,
        BoltInspector inspector,
        InspectionWork inspectionWork,
        Func<bool> captureEnabled,
        Func<IReadOnlyList<BoltPoint>> boltPoints)
    {
        _settings = settings;
        _segmenter = segmenter;
        _session = session;
        _operations = operations;
        _inspector = inspector;
        _inspectionWork = inspectionWork;
        _captureEnabled = captureEnabled;
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
    [NotifyCanExecuteChangedFor(nameof(AddImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyLabelCommand))]
    [NotifyPropertyChangedFor(nameof(ConfigurationEnabled))]
    [NotifyPropertyChangedFor(nameof(DatasetEditingEnabled))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyLabelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureBoltPointsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
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

    private bool HasLabelImage => LabelImage is not null;
    public bool ConfigurationEnabled => !IsBusy && !HasLabelImage;
    public bool DatasetEditingEnabled => !IsBusy;

    public void Activate()
    {
        CaptureBoltPointsCommand.NotifyCanExecuteChanged();
        _session.SetRunning(IsBusy || HasLabelImage);
    }

    public void Deactivate()
    {
        CaptureBoltPointsCommand.Cancel();
        AddImagesCommand.Cancel();
        TrainCommand.Cancel();
        if (!IsBusy)
        {
            _session.SetRunning(false);
        }
    }

    public async Task ShutdownAsync()
    {
        var pending = new[]
            {
                CaptureBoltPointsCommand.ExecutionTask,
                AddImagesCommand.ExecutionTask,
                TrainCommand.ExecutionTask,
            }
            .OfType<Task>()
            .Where(task => !task.IsCompleted)
            .ToArray();
        try
        {
            Deactivate();
        }
        finally
        {
            await Task.WhenAll(pending);
        }
    }

    [RelayCommand(CanExecute = nameof(DatasetEditingEnabled))]
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

    [RelayCommand(CanExecute = nameof(ConfigurationEnabled))]
    private async Task AddImagesAsync(string[]? files, CancellationToken cancellationToken)
    {
        if (files is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add Bolt Images",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            files = dialog.FileNames;
        }

        Begin(BoltTrainingState.LoadingImages);
        try
        {
            var images = await Task.Run(() =>
            {
                var captures = new List<LabelCapture>(files.Length);
                foreach (var path in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var image = BitmapFiles.LoadFrame(path);
                    var size = IBoltRecessSegmenter.InputSize;
                    if (image.Width < size || image.Height < size)
                    {
                        throw new InvalidOperationException(
                            $"{Path.GetFileName(path)} must be at least {size} x {size} pixels.");
                    }

                    captures.Add(new LabelCapture(Path.GetFileName(path), image));
                }

                cancellationToken.ThrowIfCancellationRequested();
                return captures;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            BeginLabeling(images);
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

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureBoltPointsAsync(
        CancellationToken cancellationToken)
    {
        Begin(BoltTrainingState.Capturing);
        try
        {
            using var operation = _operations.Link(cancellationToken);
            cancellationToken = operation.Token;
            var points = _boltPoints()
                .Where(point =>
                    _inspectionWork.HeatSinkPresent(point.HeatSink))
                .ToArray();
            var images = new List<LabelCapture>();
            foreach (var point in points.OrderBy(point => point.Number))
            {
                images.Add(new LabelCapture(
                    $"{point.HeatSink.GetDescription()} · Bolt {point.Number}",
                    await _inspector.CaptureAsync(
                        point,
                        cancellationToken)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            BeginLabeling(images);
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
        Review = null;
        Begin(BoltTrainingState.LoadingData);
        Epoch = 0;
        TrainingLoss = 0;
        ValidationLoss = 0;

        try
        {
            Review = await TrainModelAsync(cancellationToken);
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

    private async Task<BoltModelReview> TrainModelAsync(CancellationToken cancellationToken)
    {
        var modelFile = Path.Combine(AppContext.BaseDirectory, _settings.ModelFile);
        var trainingFile = modelFile + ".training";
        try
        {
            var dataset = await Task.Run(
                () => new BoltSegmentationDataset(
                    DatasetDirectory,
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
                    trainingFile,
                    Epochs,
                    progress,
                    cancellationToken),
                cancellationToken);

            var review = await Task.Run(
                () => BoltModelReview.Load(
                        trainingFile,
                        dataset.Validation,
                        _settings,
                        cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            State = BoltTrainingState.Applying;
            await Task.Run(async () =>
            {
                File.Move(trainingFile, modelFile, overwrite: true);
                _segmenter.Reload();
                review.CalibrateMinimumMaskRatio();
                await _settings.SaveAsync();
            });
            return review;
        }
        finally
        {
            File.Delete(trainingFile);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        CaptureBoltPointsCommand.Cancel();
        AddImagesCommand.Cancel();
        TrainCommand.Cancel();
        if (!IsBusy)
        {
            BeginLabeling([]);
            Error = null;
            State = BoltTrainingState.Cancelled;
            _session.SetRunning(false);
        }
    }

    private bool CanCapture()
    {
        if (!_captureEnabled()
            || !ConfigurationEnabled
            || !_inspectionWork.CarrierSeated)
        {
            return false;
        }

        var points = _boltPoints()
            .Where(point => _inspectionWork.HeatSinkPresent(point.HeatSink))
            .ToArray();
        return points.Length > 0
            && points.All(_inspector.HasPosition);
    }

    private bool CanTrain() =>
        ConfigurationEnabled
        && Epochs > 0
        && !string.IsNullOrWhiteSpace(DatasetDirectory);

    private bool CanLabel() => !IsBusy && HasLabelImage;
    private bool CanCancel() => !IsBusy && HasLabelImage
                               || IsBusy && State is BoltTrainingState.Capturing
                                   or BoltTrainingState.LoadingImages
                                   or BoltTrainingState.LoadingData
                                   or BoltTrainingState.Training;
    private void SaveLabel(byte[] mask)
    {
        try
        {
            BoltTrainingFiles.SaveSample(
                DatasetDirectory,
                LabelImage!,
                mask);
        }
        catch (Exception exception)
        {
            Fail(exception);
            return;
        }

        Review = null;
        Error = null;
        State = BoltTrainingState.Ready;
        _labelIndex++;
        ShowLabelImage();
        _session.SetRunning(HasLabelImage);
    }

    private void BeginLabeling(IReadOnlyList<LabelCapture> images)
    {
        _labelImages = images;
        _labelIndex = 0;
        SelectedImageCount = images.Count;
        ShowLabelImage();
        State = BoltTrainingState.Ready;
    }

    private void ShowLabelImage()
    {
        if (_labelIndex >= _labelImages.Count)
        {
            _labelImages = [];
            _labelIndex = 0;
            LabelImage = null;
            LabelImageName = null;
            LabelImageNumber = 0;
            SelectedImageCount = 0;
            return;
        }

        var capture = _labelImages[_labelIndex];
        LabelImage = BoltTrainingFiles.CreateInput(capture.Image);
        LabelImageName = capture.Name;
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

    partial void OnDatasetDirectoryChanged(string value) => Review = null;

    private sealed record LabelCapture(string Name, ImageFrame Image);
}
