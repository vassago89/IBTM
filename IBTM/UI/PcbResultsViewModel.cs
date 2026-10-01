using System;
using System.Collections.Generic;
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
    private readonly RecipeManager _recipes;
    private readonly InspectionImageLoader _images;
    private readonly ILogger<PcbResultsViewModel> _log;

    public PcbResultsViewModel(RecipeManager recipes, InspectionImageLoader images, ILogger<PcbResultsViewModel> log)
    {
        _recipes = recipes;
        _images = images;
        _log = log;
        LoadImagesCommand = new AsyncRelayCommand(LoadImagesAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync);
        BoltResults = [];
        Images = [];
    }

    public IAsyncRelayCommand LoadImagesCommand { get; }
    public IAsyncRelayCommand ExportCsvCommand { get; }

    [ObservableProperty]
    public partial PcbRecord? Record { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PcbBoltResultView> BoltResults { get; private set; }

    [ObservableProperty]
    public partial PcbBoltResultView? SelectedBolt { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PcbInspectionImageItem> Images { get; private set; }

    [ObservableProperty]
    public partial PcbInspectionImageItem? SelectedImage { get; set; }

    [ObservableProperty]
    public partial string? ImageError { get; private set; }

    [ObservableProperty]
    public partial string? ExportMessage { get; private set; }

    public IReadOnlyList<PcbBoltPresenceView> InspectionOnlyResults
    {
        get
        {
            if (Record is not { } record)
                return [];
            return record.BoltPresenceResults
                .Where(pair => !record.ShootingBoltResults.ContainsKey(pair.Key)
                    && !record.PickupBoltResults.ContainsKey(pair.Key))
                .Select(pair => new PcbBoltPresenceView(
                    pair.Key, record.GetBoltOrdinal(pair.Key), pair.Value, _recipes.Current))
                .OrderBy(row => row.Ordinal)
                .ToArray();
        }
    }

    partial void OnRecordChanged(PcbRecord? oldValue, PcbRecord? newValue)
    {
        OnPropertyChanged(nameof(InspectionOnlyResults));
        var selected = SelectedBolt;
        var selectedImage = SelectedImage;
        BoltResults = newValue is null ? [] : newValue.ShootingBoltResults
            .Select(pair => (pair.Key, pair.Value, Head: FasteningHead.Shooting))
            .Concat(newValue.PickupBoltResults.Select(pair => (pair.Key, pair.Value, Head: FasteningHead.Pickup)))
            .Select(row => new PcbBoltResultView(row.Key, newValue.GetBoltOrdinal(row.Key), row.Head, row.Value,
                newValue.BoltPresenceResults.TryGetValue(row.Key, out var present) ? present : null,
                _recipes.Current))
            .OrderBy(row => row.Result.RecordedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(row => row.Ordinal).ThenBy(row => row.Head).ToArray();
        SelectedBolt = BoltResults.FirstOrDefault(row => row.BoltId == selected?.BoltId && row.Head == selected.Head)
            ?? BoltResults.FirstOrDefault();
        if (oldValue?.Number != newValue?.Number || oldValue?.DatabaseFile != newValue?.DatabaseFile)
        {
            ExportMessage = null;
            Images = [];
            SelectedImage = null;
            _ = LoadImagesCommand.ExecuteAsync(null);
        }
        else
        {
            SelectedImage = selectedImage;
        }
    }

    partial void OnSelectedBoltChanged(PcbBoltResultView? value)
    {
        if (value is not null)
            SelectedImage = Images.FirstOrDefault(image => image.Record.BoltId == value.BoltId);
    }

    partial void OnSelectedImageChanged(PcbInspectionImageItem? value)
    {
        if (value is not null)
            SelectedBolt = BoltResults.FirstOrDefault(bolt => bolt.BoltId == value.Record.BoltId);
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
            AppendRow(UiText.Get("Bolt fastening"), UiText.Get(record.FasteningResult));
            AppendRow(UiText.Get("Vision inspection"), UiText.Get(record.InspectionResult));
            AppendRow(UiText.Get("Turns result"), record.TurnsResult is { } turns ? UiText.Get(turns) : UiText.Get("Not set"));
            AppendRow(UiText.Get("Torque: controller unit"));
            AppendRow(UiText.Get("Blank = not recorded"));
            csv.AppendLine();
            AppendRow(UiText.Get("Bolt results"));
            AppendRow(UiText.Get("No."), UiText.Get("Bolt name"), UiText.Get("Fastening type"),
                UiText.Get("Fasten"), UiText.Get("Vision inspection"), UiText.Get("Total turns"),
                UiText.Get("Minimum turns"), UiText.Get("Maximum turns"), UiText.Get("Turns result"), UiText.Get("Result torque"),
                UiText.Get("Target torque"), UiText.Get("Target speed (rpm)"), UiText.Get("Fastening time (ms)"),
                UiText.Get("Angle A3 (°)"), UiText.Get("Recorded at"), UiText.Get("Result source"),
                UiText.Get("Error code"), UiText.Get("Error / message"));
            var number = 0;
            foreach (var bolt in BoltResults)
            {
                var result = bolt.Result;
                var controller = result.Controller;
                AppendRow(++number, bolt.BoltLabel, bolt.HeadLabel, bolt.Verdict, bolt.VisionVerdict,
                    result.TotalTurns,
                    result.MinimumTurns, result.MaximumTurns, bolt.TurnsVerdict, result.Torque,
                    controller?.TargetTorque, controller?.TargetSpeedRpm, controller?.FasteningTimeMilliseconds,
                    controller?.Angle3, result.RecordedAt, UiText.Get(result.Source), controller?.ErrorCode,
                    string.Join(" · ", new[] { result.Error, controller?.ErrorCode > 0 ? bolt.ControllerErrorDescription : null }
                        .Where(message => !string.IsNullOrWhiteSpace(message))));
            }
            foreach (var bolt in InspectionOnlyResults)
            {
                AppendRow(++number, bolt.BoltLabel, null, UiText.Get("Not recorded"), bolt.Present ? "OK" : "NG",
                    null, null, UiText.Get("Not recorded"), null, null, null, null, null, null, null, null, null);
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
            ExportMessage = UiText.Format($"CSV export failed: {exception.Message}");
            _log.LogError(exception, "PCB {Number} CSV export failed.", record.Number);
        }
    }

    private async Task LoadImagesAsync(CancellationToken cancellationToken)
    {
        var record = Record;
        ImageError = null;
        if (record is null)
            return;
        try
        {
            var images = await _images.LoadRecordAsync(record, _recipes.Current, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            var hasSelection = SelectedImage is not null || SelectedBolt is not null;
            var selectedBoltId = SelectedImage is { } selected
                ? selected.Record.BoltId : SelectedBolt?.BoltId;
            Images = images;
            var failed = images.Where(image => image.Error is not null).Select(image => image.Title).ToArray();
            ImageError = failed.Length == 0 ? null : UiText.Format($"Image unavailable: {string.Join(", ", failed)}");
            SelectedImage = hasSelection
                ? images.FirstOrDefault(image => image.Record.BoltId == selectedBoltId)
                : images.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            ImageError = UiText.Format($"Inspection images could not be loaded: {exception.Message}");
            _log.LogError(exception, "PCB {Number} image history load failed.", record.Number);
        }
    }
}

public sealed record PcbBoltPresenceView(Guid BoltId, int? Ordinal, bool Present, Recipe Recipe)
{
    public string BoltLabel => Recipe.Pcb.GetBoltName(BoltId, Ordinal);
}

public sealed record PcbBoltResultView(
    Guid BoltId, int? Ordinal, FasteningHead Head, BoltResult Result, bool? Present, Recipe Recipe)
{
    public string HeadLabel => UiText.Get(Head);
    public string BoltLabel => Recipe.Pcb.GetBoltName(BoltId, Ordinal);
    public string Title => $"{BoltLabel} · {HeadLabel}";
    public string Verdict => Result.Source == BoltResultSource.DryRun ? UiText.Get("DRY RUN") : Result.Success ? "OK" : "NG";
    public string VisionVerdict => Present is not { } present ? "—" : present ? "OK" : "NG";

    public string TurnsVerdict
    {
        get
        {
            switch (Result.TurnsResult)
            {
                case AssemblyResult.Ok:
                    return "OK";
                case AssemblyResult.Ng:
                    return "NG";
                case AssemblyResult.Pending:
                    return UiText.Get("No data");
                default:
                    return UiText.Get("Not set");
            }
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
