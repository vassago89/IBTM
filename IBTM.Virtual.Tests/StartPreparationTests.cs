using System.Windows;
using IBTM.UI;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class StartPreparationTests
{
    [Fact]
    public void PlanResumesAtTheCancelledPreparation()
    {
        var accepted = new TestPreparation { Result = true };
        var cancelled = new TestPreparation();
        var following = new TestPreparation { Result = true };
        var plan = new StartPreparationPlan([
            accepted,
            cancelled,
            following,
        ]);

        Assert.False(plan.Prepare(null!));
        Assert.Equal(1, accepted.OpenCount);
        Assert.Equal(1, cancelled.OpenCount);
        Assert.Equal(0, following.OpenCount);

        cancelled.Result = true;

        Assert.True(plan.Prepare(null!));
        Assert.Equal(1, accepted.OpenCount);
        Assert.Equal(2, cancelled.OpenCount);
        Assert.Equal(1, following.OpenCount);
    }

    private sealed class TestPreparation : StartPreparation
    {
        public override StartPreparationType Type =>
            StartPreparationType.BoltFasteningRecovery;
        public override bool Required => true;
        public bool Result { get; set; }
        public int OpenCount { get; private set; }

        protected override bool Show(Window owner)
        {
            OpenCount++;
            return Result;
        }
    }
}
