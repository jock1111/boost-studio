using BoostStudio.Core.Dsp;

namespace BoostStudio.Core.Tests;

public sealed class AutoGainControllerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DisabledControllerIgnoresPeaks()
    {
        var controller = new AutoGainController();
        var events = new List<double>();
        controller.TrimChanged += (_, trim) => events.Add(trim);

        controller.ProcessPeak(0, T0);
        controller.ProcessPeak(0, T0.AddSeconds(1));
        controller.ProcessPeak(0, T0.AddSeconds(2));

        Assert.False(controller.IsEnabled);
        Assert.Equal(0, controller.TrimDb);
        Assert.Empty(events);
        Assert.Contains("off", controller.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstFrameIsIgnoredThenPeaksApplyBoundedReduction()
    {
        var controller = new AutoGainController();
        var events = new List<double>();
        controller.TrimChanged += (_, trim) => events.Add(trim);
        controller.Configure(true);

        controller.ProcessPeak(-0.5, T0); // Establish frame timing, no action.
        Assert.Equal(0, controller.TrimDb);

        controller.ProcessPeak(-0.5, T0.AddMilliseconds(100));
        Assert.Equal(-1.5, controller.TrimDb, 3);
        Assert.Equal(2, events.Count);
        Assert.Contains("safety trim", controller.Describe(), StringComparison.OrdinalIgnoreCase);

        controller.ProcessPeak(-0.5, T0.AddMilliseconds(800));
        Assert.Equal(-3, controller.TrimDb, 3);
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public void AttacksAreRateLimited()
    {
        var controller = new AutoGainController();
        controller.Configure(true);
        controller.ProcessPeak(0, T0);

        controller.ProcessPeak(0, T0.AddMilliseconds(100));
        var afterFirst = controller.TrimDb;
        controller.ProcessPeak(0, T0.AddMilliseconds(300));
        controller.ProcessPeak(0, T0.AddMilliseconds(500));

        Assert.Equal(afterFirst, controller.TrimDb, 3);
    }

    [Fact]
    public void GainNeverGoesBelowMinus24Db()
    {
        var controller = new AutoGainController();
        controller.Configure(true);
        var now = T0;
        controller.ProcessPeak(0, now);

        for (var i = 0; i < 20; i++)
        {
            now = now.AddMilliseconds(700);
            controller.ProcessPeak(0, now);
        }

        Assert.Equal(-24, controller.TrimDb, 3);
        Assert.Contains("-24.0 dB", controller.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void QuietProgramRecoversSlowlyInHalfDbSteps()
    {
        var controller = new AutoGainController();
        controller.Configure(true);
        controller.ProcessPeak(0, T0);
        controller.ProcessPeak(0, T0.AddMilliseconds(100));
        Assert.True(controller.TrimDb < -1);

        var now = T0.AddSeconds(1);
        controller.ProcessPeak(-30, now);
        var afterAttack = controller.TrimDb;

        for (var i = 1; i <= 11; i++)
        {
            now = T0.AddSeconds(1 + i);
            controller.ProcessPeak(-30, now);
        }

        Assert.Equal(Math.Min(0, afterAttack + 0.5), controller.TrimDb, 3);
        Assert.True(controller.TrimDb > afterAttack || afterAttack == 0);
    }

    [Fact]
    public void ResetAndDisableClearTrimAndRaiseEvents()
    {
        var controller = new AutoGainController();
        controller.Configure(true);
        controller.ProcessPeak(0, T0);
        controller.ProcessPeak(0, T0.AddMilliseconds(100));
        Assert.True(controller.TrimDb < 0);

        var events = new List<double>();
        controller.TrimChanged += (_, trim) => events.Add(trim);

        controller.Reset();
        Assert.Equal(0, controller.TrimDb);
        Assert.Single(events);
        Assert.Equal(0, events[0]);

        controller.ProcessPeak(0, T0.AddMilliseconds(1000));
        controller.ProcessPeak(0, T0.AddMilliseconds(1700));
        Assert.True(controller.TrimDb < 0);

        controller.Configure(false);
        Assert.Equal(0, controller.TrimDb);
        Assert.False(controller.IsEnabled);
        Assert.Contains(0, events);
    }

    [Fact]
    public void ProcessPeakResetsStateAfterLongGap()
    {
        var controller = new AutoGainController();
        controller.Configure(true);
        controller.ProcessPeak(0, T0);
        controller.ProcessPeak(0, T0.AddMilliseconds(100));
        Assert.True(controller.TrimDb < 0);

        // A gap longer than five seconds means the measurement stream restarted;
        // the controller ignores that frame but keeps its safety trim.
        controller.ProcessPeak(0, T0.AddSeconds(20));
        Assert.True(controller.TrimDb < 0);

        controller.ProcessPeak(0, T0.AddSeconds(20.1));
        Assert.True(controller.TrimDb < 0);
    }
}
