using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace IBTM.UI;

public partial class PcbResultsViewModel : ObservableObject
{
    private readonly MachineStore _store;
    private readonly RecipeManager _recipes;
    private readonly InspectionImageLoader _images;
    private readonly ILogger<PcbResultsViewModel> _log;
    private bool _shuttingDown;

    public PcbResultsViewModel(MachineStore store, RecipeManager recipes, InspectionImageLoader images,
        ILogger<PcbResultsViewModel> log)
    {
        _store = store;
        _recipes = recipes;
        _images = images;
        _log = log;
        LoadImagesCommand = new AsyncRelayCommand(LoadImagesAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        LoadCurvesCommand = new AsyncRelayCommand(LoadCurvesAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync);
        BoltResults = [];
        HeightResults = [];
        BoltStages = [];
        Images = [];
        recipes.Changed += OnRecipeChanged;
    }

    public IAsyncRelayCommand LoadImagesCommand { get; }
    public IAsyncRelayCommand LoadCurvesCommand { get; }
    public IAsyncRelayCommand ExportCsvCommand { get; }

    [ObservableProperty]
    public partial PcbRecord? Record { get; set; }

    public ObservableCollection<PcbBoltResultView> BoltResults { get; }
    public ObservableCollection<PcbHeightResultView> HeightResults { get; }
    public ObservableCollection<PcbBoltResultView> BoltStages { get; }

    [ObservableProperty]
    public partial PcbBoltResultView? SelectedBolt { get; set; }

    [ObservableProperty]
    public partial PcbHeightResultView? SelectedHeight { get; set; }

    [ObservableProperty]
    public partial PcbBoltResultView? SelectedBoltStage { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PcbInspectionImageItem> Images { get; private set; }

    [ObservableProperty]
    public partial PcbInspectionImageItem? SelectedImage { get; set; }

    [ObservableProperty]
    public partial string? ImageError { get; private set; }

    [ObservableProperty]
    public partial string? CurveLoadError { get; private set; }

    [ObservableProperty]
    public partial string? ExportMessage { get; private set; }

    partial void OnRecordChanged(PcbRecord? oldValue, PcbRecord? newValue)
    {
        if (_shuttingDown)
            return;
        LoadCurvesCommand.Cancel();
        CurveLoadError = null;
        var recordChanged = oldValue?.Number != newValue?.Number
            || oldValue?.DatabaseFile != newValue?.DatabaseFile;
        if (recordChanged)
        {
            SelectedBolt = null;
            SelectedHeight = null;
            BoltResults.Clear();
            HeightResults.Clear();
            ExportMessage = null;
            Images = [];
            SelectedImage = null;
        }
        RefreshResults();
        if (recordChanged)
            _ = LoadImagesCommand.ExecuteAsync(null);
        if (newValue is { IsCurveDataLoaded: false })
            _ = LoadCurvesCommand.ExecuteAsync(null);
    }

    private void RefreshResults()
    {
        var record = Record;
        var recipe = record is not null && MachineStore.IsSameRecipeName(record.RecipeName, _recipes.Current.Name)
            ? _recipes.Current : null;
        var heights = record?.HeightResults.OrderBy(row => record.GetBoltOrdinal(row.Key)).ToArray() ?? [];
        for (var index = 0; index < heights.Length; index++)
        {
            var height = heights[index];
            var ordinal = record!.GetBoltOrdinal(height.Key);
            var label = recipe?.Pcb.GetBoltName(height.Key, ordinal) ?? BoltPoint.GetDisplayName(null, ordinal);
            var row = HeightResults.FirstOrDefault(row => row.BoltId == height.Key);
            if (row is null)
                HeightResults.Insert(index, new(height.Key, ordinal, label, height.Value));
            else
            {
                var previousIndex = HeightResults.IndexOf(row);
                if (previousIndex != index)
                    HeightResults.Move(previousIndex, index);
                row.Result = height.Value;
                row.BoltLabel = label;
            }
        }
        while (HeightResults.Count > heights.Length)
            HeightResults.RemoveAt(HeightResults.Count - 1);
        if (SelectedHeight is not null && !HeightResults.Contains(SelectedHeight))
            SelectedHeight = null;
        var results = record is null ? [] : record.ShootingBoltResults
            .Select(pair => (pair.Key, pair.Value, Head: FasteningHead.Shooting))
            .Concat(record.PickupBoltResults.Select(pair => (pair.Key, pair.Value, Head: FasteningHead.Pickup)))
            .OrderBy(row => row.Value.PreviousFinalResult?.PreliminaryResult?.RecordedAt
                ?? row.Value.PreliminaryResult?.RecordedAt ?? row.Value.RecordedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(row => record.GetBoltOrdinal(row.Key)).ThenBy(row => row.Head).ToArray();
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            var row = BoltResults.FirstOrDefault(row => row.BoltId == result.Key && row.Head == result.Head);
            if (row is null)
            {
                BoltResults.Insert(index, new(result.Key, record!.GetBoltOrdinal(result.Key), result.Head,
                    result.Value, recipe));
            }
            else
            {
                var previousIndex = BoltResults.IndexOf(row);
                if (previousIndex != index)
                    BoltResults.Move(previousIndex, index);
                row.Result = result.Value;
                row.Recipe = recipe;
            }
        }
        while (BoltResults.Count > results.Length)
            BoltResults.RemoveAt(BoltResults.Count - 1);
        if (SelectedBolt is null || !BoltResults.Contains(SelectedBolt))
            SelectedBolt = SelectedImage is { Record.BoltId: null } ? null : BoltResults.FirstOrDefault();
        else
            RefreshStages();
    }

    private void RefreshStages()
    {
        var stages = SelectedBolt?.StageResults.ToArray() ?? [];
        for (var index = 0; index < stages.Length; index++)
        {
            var stage = stages[index];
            var row = BoltStages.FirstOrDefault(row => row.Result.Stage == stage.Result.Stage);
            if (row is null)
            {
                BoltStages.Insert(index, new(stage.BoltId, stage.Ordinal, stage.Head,
                    stage.Result, stage.Recipe));
            }
            else
            {
                var previousIndex = BoltStages.IndexOf(row);
                if (previousIndex != index)
                    BoltStages.Move(previousIndex, index);
                row.Result = stage.Result;
                row.Recipe = stage.Recipe;
            }
        }
        while (BoltStages.Count > stages.Length)
            BoltStages.RemoveAt(BoltStages.Count - 1);
        if (SelectedBoltStage is null || !BoltStages.Contains(SelectedBoltStage))
            SelectedBoltStage = BoltStages.LastOrDefault();
    }

    partial void OnSelectedBoltChanged(PcbBoltResultView? value)
    {
        SelectedBoltStage = null;
        BoltStages.Clear();
        RefreshStages();
        if (value is not null)
        {
            SelectedImage = Images.FirstOrDefault(image => image.Record.BoltId == value.BoltId);
            SelectedHeight = HeightResults.FirstOrDefault(row => row.BoltId == value.BoltId);
        }
    }

    partial void OnSelectedHeightChanged(PcbHeightResultView? value)
    {
        if (value is null)
            return;
        SelectedImage = Images.FirstOrDefault(image => image.Record.BoltId == value.BoltId);
        if (SelectedBolt?.BoltId != value.BoltId)
            SelectedBolt = BoltResults.FirstOrDefault(row => row.BoltId == value.BoltId);
    }

    partial void OnSelectedImageChanged(PcbInspectionImageItem? value)
    {
        if (value is not null && SelectedBolt?.BoltId != value.Record.BoltId)
            SelectedBolt = BoltResults.FirstOrDefault(bolt => bolt.BoltId == value.Record.BoltId);
        if (value is not null && SelectedHeight?.BoltId != value.Record.BoltId)
            SelectedHeight = HeightResults.FirstOrDefault(row => row.BoltId == value.Record.BoltId);
    }

    private void OnRecipeChanged()
    {
        if (_shuttingDown)
            return;
        var selected = SelectedImage?.Record;
        RefreshResults();
        var recipe = Record is { } record && MachineStore.IsSameRecipeName(record.RecipeName, _recipes.Current.Name)
            ? _recipes.Current : null;
        Images = Images.Select(image => image with { Recipe = recipe }).ToArray();
        SelectedImage = Images.FirstOrDefault(image => ReferenceEquals(image.Record, selected));
    }

    public Task ShutdownAsync()
    {
        _shuttingDown = true;
        _recipes.Changed -= OnRecipeChanged;
        return CommandShutdown.CancelAndWaitAsync([LoadImagesCommand, LoadCurvesCommand, ExportCsvCommand]);
    }

    private async Task ExportCsvAsync()
    {
        if (Record is not { } record)
            return;

        ExportMessage = null;
        try
        {
            var barcode = string.IsNullOrWhiteSpace(record.PcbBarcode) ? "NO_READ" : record.PcbBarcode;
            var invalidCharacters = Path.GetInvalidFileNameChars();
            barcode = new string(barcode.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
            var dialog = new SaveFileDialog
            {
                Title = UiText.Get("Export CSV"),
                Filter = "CSV (*.csv)|*.csv",
                DefaultExt = ".csv",
                FileName = $"{record.CreatedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}_{barcode}_{record.Result.ToString().ToUpperInvariant()}.csv",
            };

            var csv = new StringBuilder();
            AppendRow(UiText.Get("PCB results"), record.Number);
            AppendRow(UiText.Get("Recipe"), record.RecipeName);
            AppendRow(UiText.Get("Heat sink"), UiText.Get(record.HeatSink));
            AppendRow(UiText.Get("Created at"), record.CreatedAt);
            AppendRow(UiText.Get("Updated at"), record.UpdatedAt);
            AppendRow(UiText.Get("Result"), UiText.Get(record.Result));
            AppendRow(UiText.Get("Data Matrix"), record.PcbBarcode ?? UiText.Get("Not read"), UiText.Get(record.PcbBarcodeResult));
            if (record.IsDataMatrixNgExcluded)
                AppendRow(UiText.Get("Data Matrix"), UiText.Get("Excluded from NG"));
            AppendRow(UiText.Get("Bolt fastening"), UiText.Get(record.FasteningResult));
            AppendRow(UiText.Get("Torque (controller unit)"));
            AppendRow(UiText.Get("Blank = not recorded"));
            csv.AppendLine();
            AppendRow(UiText.Get("Bolt results"));
            AppendRow(UiText.Get("No."), UiText.Get("Bolt name"), UiText.Get("Fastening type"),
                UiText.Get("Stage"), UiText.Get("Fasten"), UiText.Get("Combined turns"),
                UiText.Get("Torque"),
                UiText.Get("Target torque"), UiText.Get("Target speed (rpm)"), UiText.Get("Time (ms)"),
                UiText.Get("Angle A3 (°)"), UiText.Get("Recorded at"), UiText.Get("Result source"),
                UiText.Get("Error code"), UiText.Get("Error / message"), UiText.Get("Z feed distance (mm)"));
            var number = 0;
            foreach (var bolt in BoltResults)
            {
                var result = bolt.Result;
                var controller = result.Controller;
                AppendRow(++number, bolt.BoltLabel, bolt.HeadLabel, UiText.Get(result.Stage), bolt.Verdict,
                    result.TotalTurns,
                    result.CompensatedTorque,
                    controller?.CompensatedTargetTorque, controller?.TargetSpeedRpm, controller?.FasteningTimeMilliseconds,
                    controller?.Angle3, result.RecordedAt, UiText.Get(result.Source), controller?.ErrorCode,
                    string.Join(" ", new[] { result.Error, controller?.ErrorCode > 0 ? bolt.ControllerErrorDescription : null }
                        .Where(message => !string.IsNullOrWhiteSpace(message))), result.ZFeedDistance);
            }
            csv.AppendLine();
            AppendRow(UiText.Get("Fastening stages"));
            AppendRow(UiText.Get("No."), UiText.Get("Bolt name"), UiText.Get("Stage"), UiText.Get("Preset"),
                UiText.Get("Result"), UiText.Get("Stage turns"), UiText.Get("Torque"), UiText.Get("Target torque"),
                UiText.Get("Target speed (rpm)"), UiText.Get("Time (ms)"), UiText.Get("Angle A3 (°)"),
                UiText.Get("Recorded at"), UiText.Get("Result source"), UiText.Get("Error code"), UiText.Get("Error / message"),
                UiText.Get("Z feed distance (mm)"));
            number = 0;
            foreach (var stage in BoltResults.SelectMany(bolt => bolt.StageResults).OrderBy(row => row.Result.RecordedAt))
            {
                var result = stage.Result;
                var controller = result.Controller;
                AppendRow(++number, stage.BoltLabel, UiText.Get(result.Stage), controller?.Preset,
                    stage.StageVerdict, result.MeasuredTurns, result.CompensatedTorque, controller?.CompensatedTargetTorque,
                    controller?.TargetSpeedRpm, controller?.FasteningTimeMilliseconds, controller?.Angle3,
                    result.RecordedAt, UiText.Get(result.Source), controller?.ErrorCode,
                    string.Join(" ", new[] { result.Error, controller?.ErrorCode > 0 ? stage.ControllerErrorDescription : null }
                        .Where(message => !string.IsNullOrWhiteSpace(message))), result.ZFeedDistance);
            }

            csv.AppendLine();
            if (record.FiducialError is { } fiducialError)
            {
                AppendRow(UiText.Get("Fiducial alignment"), "NG", fiducialError,
                    UiText.Get("Exclude fiducial from NG"), record.IsFiducialNgExcluded);
                csv.AppendLine();
            }
            else if (record.Alignment is { } alignment)
            {
                AppendRow(UiText.Get("Fiducial alignment"), UiText.Get("Rotation (°)"), alignment.AngleDegrees, alignment.RecordedAt);
                AppendRow(UiText.Get("No."), UiText.Get("Reference X (mm)"), UiText.Get("Reference Y (mm)"),
                    UiText.Get("Measured X (mm)"), UiText.Get("Measured Y (mm)"), UiText.Get("Shape error"));
                AppendRow(1, alignment.First.Reference.X, alignment.First.Reference.Y, alignment.First.Measured.X, alignment.First.Measured.Y, alignment.First.ShapeError);
                AppendRow(2, alignment.Second.Reference.X, alignment.Second.Reference.Y, alignment.Second.Measured.X, alignment.Second.Measured.Y, alignment.Second.ShapeError);
                csv.AppendLine();
            }
            AppendRow(UiText.Get("Reference plane"), UiText.Get("X (mm)"), UiText.Get("Y (mm)"),
                UiText.Get("Measured height (mm)"), UiText.Get("Recorded at"));
            if (record.HeightReference is { } plane)
            {
                AppendRow(1, plane.Point1.X, plane.Point1.Y, plane.Point1.Value, plane.RecordedAt);
                AppendRow(2, plane.Point2.X, plane.Point2.Y, plane.Point2.Value, plane.RecordedAt);
                AppendRow(3, plane.Point3.X, plane.Point3.Y, plane.Point3.Value, plane.RecordedAt);
            }
            csv.AppendLine();
            AppendRow(UiText.Get("Height measurement"), UiText.Get("Result"), UiText.Get("Height (mm)"),
                UiText.Get("Minimum height (mm)"), UiText.Get("Maximum height (mm)"),
                UiText.Get("Measured height (mm)"), UiText.Get("Reference height (mm)"), UiText.Get("Recorded at"));
            foreach (var row in HeightResults)
            {
                var height = row.Result;
                AppendRow(row.Ordinal, height.Success ? "OK" : "NG", height.Height, height.Minimum, height.Maximum,
                    height.Sample.Value, height.ReferenceHeight, height.RecordedAt);
            }

            if (dialog.ShowDialog() != true)
                return;
            await File.WriteAllTextAsync(dialog.FileName, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ExportMessage = UiText.Format($"CSV saved: {dialog.FileName}");

            void AppendRow(params object?[] values)
            {
                for (var index = 0; index < values.Length; index++)
                {
                    if (index > 0)
                        csv.Append(',');
                    var text = values[index] switch
                    {
                        DateTimeOffset time => time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        double number => number.ToString("0.###", CultureInfo.InvariantCulture),
                        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                        { } value => value.ToString()!,
                        _ => string.Empty,
                    };
                    // Names and barcodes are text, even when they begin with an Excel formula character.
                    if (values[index] is string && text.TrimStart() is ['=' or '+' or '-' or '@', ..])
                        text = "'" + text;
                    if (text.IndexOfAny([',', '"', '\r', '\n']) >= 0)
                        csv.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
                    else
                        csv.Append(text);
                }
                csv.AppendLine();
            }
        }
        catch (Exception exception)
        {
            ExportMessage = UiText.Format($"CSV export failed: {UiText.Get(exception)}");
            _log.LogError(exception, "PCB {Number} CSV export failed.", record.Number);
        }
    }

    private async Task LoadCurvesAsync(CancellationToken cancellationToken)
    {
        var previous = LoadCurvesCommand.ExecutionTask;
        var record = Record;
        try
        {
            if (_shuttingDown || record is not { IsCurveDataLoaded: false })
                return;
            var loaded = await Task.Run(() => _store.LoadPcb(record), cancellationToken);
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(Record, record))
                Record = loaded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(Record, record))
                return;
            CurveLoadError = UiText.Get(exception);
            _log.LogError(exception, "PCB {Number} graph history load failed.", record?.Number);
        }
        finally
        {
            if (previous is { IsCompleted: false })
                await previous;
        }
    }

    private async Task LoadImagesAsync(CancellationToken cancellationToken)
    {
        // The command still exposes the previous invocation while starting this one.
        var previous = LoadImagesCommand.ExecutionTask;
        var record = Record;
        try
        {
            if (_shuttingDown)
                return;
            ImageError = null;
            if (record is null)
                return;
            var images = await _images.LoadRecordAsync(record, _recipes.Current, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            var hasSelection = SelectedImage is not null || SelectedBolt is not null;
            var selectedBoltId = SelectedImage is { } selected
                ? selected.Record.BoltId : SelectedBolt?.BoltId;
            // Recipe selection can change while the DB read is completing.
            var recipe = MachineStore.IsSameRecipeName(record.RecipeName, _recipes.Current.Name)
                ? _recipes.Current : null;
            Images = images.Select(image => image with { Recipe = recipe }).ToArray();
            var failed = Images.Where(image => image.Error is not null).Select(image => image.Title).ToArray();
            ImageError = failed.Length == 0 ? null : UiText.Format($"Image unavailable: {string.Join(", ", failed)}");
            SelectedImage = hasSelection
                ? Images.FirstOrDefault(image => image.Record.BoltId == selectedBoltId)
                : Images.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            ImageError = UiText.Format($"Inspection images could not be loaded: {UiText.Get(exception)}");
            _log.LogError(exception, "PCB {Number} image history load failed.", record?.Number);
        }
        finally
        {
            // Publish the new selection first, but keep cancelled reads owned until shutdown can drain them.
            if (previous is { IsCompleted: false })
                await previous;
        }
    }
}

public sealed partial class PcbBoltResultView : ObservableObject
{
    public PcbBoltResultView(Guid boltId, int? ordinal, FasteningHead head,
        BoltResult result, Recipe? recipe)
    {
        BoltId = boltId;
        Ordinal = ordinal;
        Head = head;
        Result = result;
        Recipe = recipe;
    }

    public Guid BoltId { get; }
    public int? Ordinal { get; }
    public FasteningHead Head { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadLabel), nameof(Title), nameof(Verdict), nameof(StageVerdict),
        nameof(ControllerStatus), nameof(Direction), nameof(ControllerErrorDescription), nameof(RegisterText))]
    public partial BoltResult Result { get; set; }

    public Recipe? Recipe
    {
        get;
        set
        {
            field = value;
            // Recipe edits also change names on the existing recipe instance.
            OnPropertyChanged(nameof(BoltLabel));
            OnPropertyChanged(nameof(Title));
        }
    }

    public string HeadLabel => UiText.Get(Result.ToolHead ?? Head);
    public string BoltLabel => Recipe?.Pcb.GetBoltName(BoltId, Ordinal) ?? BoltPoint.GetDisplayName(null, Ordinal);
    public string Title => $"{BoltLabel} {HeadLabel}";
    public string Verdict => Result.Source == BoltResultSource.Interrupted ? UiText.Get("Fastening interrupted")
        : !Result.IsComplete
        ? UiText.Get(Result.Stage == BoltFasteningStage.FinalBeforeRetightening ? "Retightening pending" : "Final tightening pending")
        : Result.Source == BoltResultSource.DryRun ? StageVerdict
        : Result.Stage == BoltFasteningStage.Preliminary ? UiText.Get("Pre-tightening NG")
        : StageVerdict;

    public string StageVerdict => Result.Source == BoltResultSource.Interrupted ? UiText.Get("Fastening interrupted")
        : Result.Source == BoltResultSource.DryRun
        ? UiText.Get("Dry run · NG") : Result.Success ? "OK" : "NG";

    public IEnumerable<PcbBoltResultView> StageResults
    {
        get
        {
            if ((Result.PreliminaryResult ?? Result.PreviousFinalResult?.PreliminaryResult) is { } preliminary)
                yield return new(BoltId, Ordinal, Head, preliminary, Recipe);
            if (Result.PreviousFinalResult is { } previousFinal)
                yield return new(BoltId, Ordinal, Head, previousFinal, Recipe);
            yield return this;
        }
    }

    public string? ControllerStatus
    {
        get
        {
            return Result.Controller is { } data
                ? $"{UiText.Get((AdcEventStatus)data.StatusCode)} ({data.StatusCode})" : null;
        }
    }

    public string? Direction
    {
        get
        {
            return Result.Controller is { } data
                ? $"{UiText.Get((AdcDirection)data.DirectionCode)} ({data.DirectionCode})" : null;
        }
    }

    public string? ControllerErrorDescription
    {
        get
        {
            return Result.Controller is { } data
                ? AdcControllerError.Describe(data.ErrorCode) : null;
        }
    }

    public string RegisterText
    {
        get
        {
            return Result.Controller?.Registers is { } registers
                ? string.Join("  ", registers.Select((value, index) => $"{(ushort)AdcResultRegister.EventCount + index}: {value:X4}"))
                : UiText.Get("Not recorded");
        }
    }
}

public partial class PcbHeightResultView : ObservableObject
{
    public PcbHeightResultView(Guid boltId, int? ordinal, string boltLabel, BoltHeightResult result)
    {
        BoltId = boltId;
        Ordinal = ordinal;
        BoltLabel = boltLabel;
        Result = result;
    }

    public Guid BoltId { get; }
    public int? Ordinal { get; }
    [ObservableProperty]
    public partial string BoltLabel { get; set; }

    [ObservableProperty]
    public partial BoltHeightResult Result { get; set; }
}
