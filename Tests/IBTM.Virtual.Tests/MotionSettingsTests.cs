using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using IBTM.BoltFastening;
using IBTM.Device;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MotionSettingsTests
{
    [Fact]
    public async Task FasteningFeedTimesDefaultToAxisSettingsAndPersistIndependently()
    {
        var legacy = JsonSerializer.Deserialize<BoltFasteningSettings>("{}")!;
        Assert.Null(legacy.FeedAccelerationSeconds);
        Assert.Null(legacy.FeedDecelerationSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => legacy.FeedAccelerationSeconds = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => legacy.FeedDecelerationSeconds = double.NaN);

        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.BoltFastening.FeedAccelerationSeconds = 0.1;
        settings.BoltFastening.FeedDecelerationSeconds = 0.2;
        settings.BoltFastening.Motion.ZAccelerationSeconds = 0.3;
        settings.BoltFastening.Motion.ZDecelerationSeconds = 0.4;
        await store.SaveSettingsAsync(settings.Sections);
        var loaded = await MachineSettings.LoadAsync(store);
        Assert.Equal(0.1, loaded.BoltFastening.FeedAccelerationSeconds);
        Assert.Equal(0.2, loaded.BoltFastening.FeedDecelerationSeconds);
        Assert.Equal(0.3, loaded.BoltFastening.Motion.ZAccelerationSeconds);
        Assert.Equal(0.4, loaded.BoltFastening.Motion.ZDecelerationSeconds);
        loaded.BoltFastening.FeedAccelerationSeconds = null;
        loaded.BoltFastening.FeedDecelerationSeconds = null;
        Assert.Null(loaded.BoltFastening.FeedAccelerationSeconds);
        Assert.Null(loaded.BoltFastening.FeedDecelerationSeconds);
    }

    [Fact]
    public async Task MotionPollIntervalDefaultsValidatesAndPersists()
    {
        var options = JsonSerializer.Deserialize<MachineOptions>("{}")!;
        Assert.Equal(50, options.MotionPollMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MotionPollMilliseconds = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MotionPollMilliseconds = 1_001);
        Assert.Equal(50, options.MotionPollMilliseconds);

        var store = VirtualTestSupport.OpenMachineStore();
        var settings = new MachineSettings();
        settings.Options.MotionPollMilliseconds = 125;
        await store.SaveSettingsAsync(settings.Sections);
        var loaded = await MachineSettings.LoadAsync(store);
        Assert.Equal(125, loaded.Options.MotionPollMilliseconds);
    }

    [Fact]
    public void ExistingAxisSettingsKeepControllerHomeSignal()
    {
        var settings = JsonSerializer.Deserialize<AxisHardware>(
            """{"Number":9,"HomeDirection":"Positive","MoveUnit":1,"MovePulse":10}""")!;

        Assert.Equal(HomeSignal.ControllerSetting, settings.HomeSignal);
        Assert.Equal(HomeDirection.Positive, settings.HomeDirection);
        Assert.Equal(10, settings.MovePulse);
    }

    [Fact]
    public void ExistingMotionTimesLoadWithoutZOverrides()
    {
        var settings = JsonSerializer.Deserialize<MotionSettings>(
            """{"AccelerationSeconds":1,"DecelerationSeconds":0.7}""")!;
        Assert.Equal(1, settings.AccelerationSeconds);
        Assert.Equal(0.7, settings.DecelerationSeconds);
        Assert.Null(settings.ZAccelerationSeconds);
        Assert.Null(settings.ZDecelerationSeconds);
        Assert.Null(settings.GetValidationError(hasZ: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidMotionSettingsReportErrorsUntilCorrected(double value)
    {
        var settings = new MotionSettings { HorizontalSpeed = value };
        settings.ZHome.SearchSpeed = value;
        settings.ZAccelerationSeconds = value;
        settings.ZDecelerationSeconds = value;
        Assert.Equal(value, settings.HorizontalSpeed);
        Assert.Contains(nameof(settings.HorizontalSpeed), settings.GetValidationError(hasZ: true));
        Assert.NotNull(settings[nameof(settings.HorizontalSpeed)]);
        Assert.NotNull(settings.ZHome[nameof(settings.ZHome.SearchSpeed)]);
        Assert.NotNull(settings[nameof(settings.ZAccelerationSeconds)]);
        Assert.NotNull(settings[nameof(settings.ZDecelerationSeconds)]);

        settings.HorizontalSpeed = 100;
        Assert.Null(settings.GetValidationError(hasZ: false));
        Assert.NotNull(settings.GetValidationError(hasZ: true));
        settings.ZHome.SearchSpeed = 10;
        settings.ZAccelerationSeconds = 0.2;
        settings.ZDecelerationSeconds = 0.3;
        Assert.Null(settings.GetValidationError(hasZ: true));
        Assert.Empty(((IDataErrorInfo)settings).Error);
    }

    [Fact]
    public async Task ZTimeBindingAcceptsIndependentValuesAndBlankFallback()
    {
        await VirtualTestSupport.RunOnStaAsync(() =>
        {
            var settings = new MotionSettings { AccelerationSeconds = 1, DecelerationSeconds = 1 };
            foreach (var property in new[] { nameof(settings.ZAccelerationSeconds), nameof(settings.ZDecelerationSeconds) })
            {
                var input = new TextBox();
                input.SetBinding(TextBox.TextProperty, new Binding(property)
                {
                    Source = settings,
                    TargetNullValue = string.Empty,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                    ValidatesOnDataErrors = true,
                });
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(string.Empty, input.Text);
                input.Text = "0";
                input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.True(Validation.GetHasError(input));
                input.Text = "0.2";
                input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.False(Validation.GetHasError(input));
                Assert.Equal(0.2, (double?)typeof(MotionSettings).GetProperty(property)!.GetValue(settings));
                input.Text = string.Empty;
                input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.False(Validation.GetHasError(input));
                Assert.Null(typeof(MotionSettings).GetProperty(property)!.GetValue(settings));
            }
            Assert.Equal(1, settings.AccelerationSeconds);
            Assert.Equal(1, settings.DecelerationSeconds);
        });
    }

    [Fact]
    public async Task InvalidSpeedBindingShowsSavedAndEditedErrorsUntilCorrected()
    {
        await VirtualTestSupport.RunOnStaAsync(() =>
        {
            var settings = new MotionSettings { ZSpeed = 0 };
            var input = new TextBox();
            input.SetBinding(TextBox.TextProperty, new Binding(nameof(settings.ZSpeed))
            {
                Source = settings,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                ValidatesOnDataErrors = true,
            });
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(Validation.GetHasError(input));
            Assert.Equal("0", input.Text);
            input.Text = "-1";
            Assert.True(Validation.GetHasError(input));
            Assert.Equal(-1, settings.ZSpeed);
            input.Text = "25";
            Assert.False(Validation.GetHasError(input));
            Assert.Equal(25, settings.ZSpeed);
        });
    }

    [Fact]
    public async Task LegacyInvalidSettingsCanBeLoadedAndCorrectedWithoutChangingOtherValues()
    {
        var store = VirtualTestSupport.OpenMachineStore();
        var legacy = new MachineSettings();
        legacy.InspectionGantry.Motion.ZSpeed = 0; // No Z axis in this group.
        legacy.PcbSupply.Motion.HorizontalSpeed = 0;
        legacy.PcbSupply.Motion.ZHome.SearchSpeed = -1;
        legacy.PcbSupply.Motion.ZSpeed = 17;
        await store.SaveSettingsAsync(legacy.Sections);

        var loaded = await MachineSettings.LoadAsync(store);
        Assert.Equal(0, loaded.InspectionGantry.Motion.ZSpeed);
        Assert.Null(loaded.InspectionGantry.Motion.GetValidationError(hasZ: false));
        Assert.Equal(0, loaded.PcbSupply.Motion.HorizontalSpeed);
        Assert.Equal(-1, loaded.PcbSupply.Motion.ZHome.SearchSpeed);
        Assert.Equal(17, loaded.PcbSupply.Motion.ZSpeed);

        using var motion = new VirtualMotionService(loaded.PcbSupply.Motion, new());
        motion.Initialize();
        var position = motion.Position;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motion.MoveToXYAsync(10, 20, loaded.PcbSupply.Motion.HorizontalSpeed));
        Assert.Equal(position, motion.Position);

        loaded.PcbSupply.Motion.HorizontalSpeed = 25;
        loaded.PcbSupply.Motion.ZHome.SearchSpeed = 5;
        Assert.Null(loaded.PcbSupply.Motion.GetValidationError(hasZ: true));
        await store.SaveSettingsAsync(loaded.Sections);
        var corrected = await MachineSettings.LoadAsync(store);
        Assert.Equal(25, corrected.PcbSupply.Motion.HorizontalSpeed);
        Assert.Equal(5, corrected.PcbSupply.Motion.ZHome.SearchSpeed);
        Assert.Equal(17, corrected.PcbSupply.Motion.ZSpeed);
        Assert.Equal(0, corrected.InspectionGantry.Motion.ZSpeed);
    }

    [Fact]
    public async Task DriveResetClearsAlarmWithoutEnablingServos()
    {
        using var motion = new VirtualMotionService(new(), new OperationCancellation());
        motion.Initialize();
        motion.SetServo(MotionAxis.Z, false);
        motion.SetAlarm(MotionAxis.Z, true);
        await motion.ResetAsync();
        var state = motion.GetAxisState(MotionAxis.Z);
        Assert.False(state.Alarm);
        Assert.False(state.ServoOn);
    }
}
