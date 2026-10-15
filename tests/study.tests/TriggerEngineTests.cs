using Study.Core.Models;
using Study.Core.Triggers;

namespace Study.Tests;

public class TriggerEngineTests
{
    private static TriggerOutcome[] Feed(TriggerEngine e, params double[] values) =>
        values.Select(e.Update).ToArray();

    [Fact]
    public void RisingEdge_FirstReadIsBaselineOnly()
    {
        var e = new TriggerEngine(TriggerMode.RisingEdge);
        Assert.Equal(TriggerOutcome.None, e.Update(1));
        Assert.Equal(AlarmState.Idle, e.State);
    }

    [Fact]
    public void RisingEdge_FiresOnZeroToOne_Only()
    {
        var e = new TriggerEngine(TriggerMode.RisingEdge);
        var r = Feed(e, 0, 0, 1, 1, 0);
        Assert.Equal([TriggerOutcome.None, TriggerOutcome.None, TriggerOutcome.Fired, TriggerOutcome.None, TriggerOutcome.None], r);
        Assert.Equal(AlarmState.Ringing, e.State); // stays ringing until acknowledged
    }

    [Fact]
    public void FallingEdge_FiresOnOneToZero()
    {
        var e = new TriggerEngine(TriggerMode.FallingEdge);
        var r = Feed(e, 1, 0);
        Assert.Equal(TriggerOutcome.Fired, r[1]);
    }

    [Fact]
    public void Edge_AcknowledgeReturnsToIdle()
    {
        var e = new TriggerEngine(TriggerMode.RisingEdge);
        Feed(e, 0, 1);
        Assert.True(e.Acknowledge());
        Assert.Equal(AlarmState.Idle, e.State);
        Assert.False(e.Acknowledge());
    }

    [Fact]
    public void WhileTrue_FiresOnFirstReadIfAlreadyTrue()
    {
        var e = new TriggerEngine(TriggerMode.WhileTrue);
        Assert.Equal(TriggerOutcome.Fired, e.Update(1));
    }

    [Fact]
    public void WhileTrue_AcknowledgeSilencesWhileStillTrue_ThenRearms()
    {
        var e = new TriggerEngine(TriggerMode.WhileTrue);
        e.Update(1);
        e.Acknowledge();
        Assert.Equal(AlarmState.Acknowledged, e.State);

        Assert.Equal(TriggerOutcome.None, e.Update(1)); // still true: no re-alarm
        Assert.Equal(AlarmState.Acknowledged, e.State);

        Assert.Equal(TriggerOutcome.Cleared, e.Update(0));
        Assert.Equal(AlarmState.Idle, e.State);

        Assert.Equal(TriggerOutcome.Fired, e.Update(1)); // re-armed
    }

    [Fact]
    public void WhileTrue_KeepsRingingAfterClear_WhenStopWhenClearedOff()
    {
        var e = new TriggerEngine(TriggerMode.WhileTrue, stopWhenCleared: false);
        e.Update(1);
        Assert.Equal(TriggerOutcome.Cleared, e.Update(0));
        Assert.Equal(AlarmState.Ringing, e.State);
    }

    [Fact]
    public void WhileTrue_StopsByItself_WhenStopWhenClearedOn()
    {
        var e = new TriggerEngine(TriggerMode.WhileTrue, stopWhenCleared: true);
        e.Update(1);
        Assert.Equal(TriggerOutcome.ClearedAndStopped, e.Update(0));
        Assert.Equal(AlarmState.Idle, e.State);
    }

    [Fact]
    public void WhileFalse_FiresWhenZero()
    {
        var e = new TriggerEngine(TriggerMode.WhileFalse);
        Assert.Equal(TriggerOutcome.None, e.Update(1));
        Assert.Equal(TriggerOutcome.Fired, e.Update(0));
    }

    [Fact]
    public void Expression_UsesPrevAndFiresOnBecomingTrue()
    {
        var e = new TriggerEngine(TriggerMode.Expression, "x > 100");
        var r = Feed(e, 50, 150, 160, 90, 120);
        Assert.Equal([TriggerOutcome.None, TriggerOutcome.Fired, TriggerOutcome.None, TriggerOutcome.Cleared, TriggerOutcome.Fired], r);
    }

    [Fact]
    public void Expression_PrevEqualsXOnFirstRead_NoSpuriousEdge()
    {
        var e = new TriggerEngine(TriggerMode.Expression, "prev < 100 && x >= 100");
        Assert.Equal(TriggerOutcome.None, e.Update(150));
    }

    [Fact]
    public void ResetBaseline_PreventsFakeEdgeAfterReconnect()
    {
        var e = new TriggerEngine(TriggerMode.RisingEdge);
        e.Update(0);
        e.ResetBaseline();
        Assert.Equal(TriggerOutcome.None, e.Update(1));
    }

    [Fact]
    public void StopWhenCleared_IgnoredForEdgeModes()
    {
        var e = new TriggerEngine(TriggerMode.RisingEdge, stopWhenCleared: true);
        Feed(e, 0, 1, 0);
        Assert.Equal(AlarmState.Ringing, e.State);
    }
}
