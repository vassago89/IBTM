using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using IBTM.Device;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MotionSettingsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidMotionSettingsReportErrorsUntilCorrected(double value)
    {
        var settings = new MotionSettings { HorizontalSpeed = value };
        settings.ZHome.SearchSpeed = value;
        Assert.Equal(value, settings.HorizontalSpeed);
        Assert.Contains(nameof(settings.HorizontalSpeed), settings.GetValidationError(hasZ: true));
        Assert.NotNull(settings[nameof(settings.HorizontalSpeed)]);
        Assert.NotNull(settings.ZHome[nameof(settings.ZHome.SearchSpeed)]);

        settings.HorizontalSpeed = 100;
        Assert.Null(settings.GetValidationError(hasZ: false));
        Assert.NotNull(settings.GetValidationError(hasZ: true));
        settings.ZHome.SearchSpeed = 10;
        Assert.Null(settings.GetValidationError(hasZ: true));
        Assert.Empty(((IDataErrorInfo)settings).Error);
    }

    [Fact]
    public async Task InvalidSpeedBindingShowsSavedAndEditedErrorsUntilCorrected()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
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
                done.SetResult();
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LegacyInvalidSettingsCanBeLoadedAndCorrectedWithoutChangingOtherValues()
    {
        var store = VirtualTest.OpenMachineStore();
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
