using OpenMacro.Engine;
using SharpHook.Data;

namespace OpenMacro.Engine.Tests;

public class MacroRecorderTests
{
    [Fact]
    public void RecordsKeysInOrderWithRealGapsAsDelays()
    {
        var time = new FakeTime();
        var recorder = new MacroRecorder(time);

        recorder.Start();
        recorder.OnKeyDown(KeyCode.VcA);
        time.AdvanceMs(100);
        recorder.OnKeyUp(KeyCode.VcA);
        time.AdvanceMs(250);
        recorder.OnKeyDown(KeyCode.VcB);

        var macro = recorder.Stop("test");

        Assert.Equal(
            [
                new KeyDownEvent(KeyCode.VcA),
                new DelayEvent(100),
                new KeyUpEvent(KeyCode.VcA),
                new DelayEvent(250),
                new KeyDownEvent(KeyCode.VcB),
            ],
            macro.Events
        );
    }

    [Fact]
    public void RecordsScrollWithDirectionAndRealGapsAsDelays()
    {
        var time = new FakeTime();
        var recorder = new MacroRecorder(time);

        recorder.Start();
        recorder.OnScroll(ScrollDirection.Up);
        time.AdvanceMs(80);
        recorder.OnScroll(ScrollDirection.Down);

        var macro = recorder.Stop("test");

        Assert.Equal(
            [
                new ScrollEvent(ScrollDirection.Up),
                new DelayEvent(80),
                new ScrollEvent(ScrollDirection.Down),
            ],
            macro.Events
        );
    }

    [Fact]
    public void NoDelayBeforeTheFirstEvent()
    {
        var time = new FakeTime();
        var recorder = new MacroRecorder(time);

        recorder.Start();
        time.AdvanceMs(5000); // long pause before the user starts typing
        recorder.OnKeyDown(KeyCode.VcA);

        var macro = recorder.Stop("test");
        Assert.Equal([new KeyDownEvent(KeyCode.VcA)], macro.Events);
    }

    [Fact]
    public void ZeroGapIsClampedToOneMillisecond()
    {
        var time = new FakeTime();
        var recorder = new MacroRecorder(time);

        recorder.Start();
        recorder.OnKeyDown(KeyCode.VcA);
        recorder.OnKeyUp(KeyCode.VcA); // same instant

        var macro = recorder.Stop("test");
        Assert.Equal(new DelayEvent(1), macro.Events[1]);
    }

    [Fact]
    public void IgnoresEventsWhenNotRecording()
    {
        var recorder = new MacroRecorder(new FakeTime());

        recorder.OnKeyDown(KeyCode.VcA); // before Start
        recorder.Start();
        var macro = recorder.Stop("test");
        recorder.OnKeyDown(KeyCode.VcB); // after Stop

        Assert.Empty(macro.Events);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void StartClearsThePreviousRecording()
    {
        var time = new FakeTime();
        var recorder = new MacroRecorder(time);

        recorder.Start();
        recorder.OnKeyDown(KeyCode.VcA);
        recorder.Stop("first");

        recorder.Start();
        recorder.OnKeyDown(KeyCode.VcB);
        var second = recorder.Stop("second");

        Assert.Equal([new KeyDownEvent(KeyCode.VcB)], second.Events);
    }

    [Fact]
    public void StepRecordedMatchesStopForMixedInput()
    {
        var time = new FakeTime();
        var recorder = new MacroRecorder(time);
        var raised = new List<MacroEvent>();
        recorder.StepRecorded += raised.Add;

        recorder.Start();
        recorder.OnKeyDown(KeyCode.VcA);
        time.AdvanceMs(40);
        recorder.OnKeyUp(KeyCode.VcA);
        time.AdvanceMs(300);
        recorder.OnMouseDown(MouseButton.Button1);
        recorder.OnMouseUp(MouseButton.Button1); // same instant: 1 ms delay
        time.AdvanceMs(1200);
        recorder.OnScroll(ScrollDirection.Down);
        time.AdvanceMs(15);
        recorder.OnScroll(ScrollDirection.Up);
        time.AdvanceMs(900); // trailing gap: never recorded

        var macro = recorder.Stop("test");

        Assert.Equal(macro.Events, raised);
        Assert.Equal(
            [
                new KeyDownEvent(KeyCode.VcA),
                new DelayEvent(40),
                new KeyUpEvent(KeyCode.VcA),
                new DelayEvent(300),
                new MouseDownEvent(MouseButton.Button1),
                new DelayEvent(1),
                new MouseUpEvent(MouseButton.Button1),
                new DelayEvent(1200),
                new ScrollEvent(ScrollDirection.Down),
                new DelayEvent(15),
                new ScrollEvent(ScrollDirection.Up),
            ],
            raised
        );
    }

    [Fact]
    public void StepRecordedIsNotRaisedWhenNotRecording()
    {
        var recorder = new MacroRecorder(new FakeTime());
        var raised = new List<MacroEvent>();
        recorder.StepRecorded += raised.Add;

        recorder.OnKeyDown(KeyCode.VcA); // before Start
        recorder.Start();
        recorder.Stop("test");
        recorder.OnKeyDown(KeyCode.VcB); // after Stop

        Assert.Empty(raised);
    }

    [Fact]
    public void SinceLastEventCountsFromTheLastRecordedEvent()
    {
        var time = new FakeTime();
        var recorder = new MacroRecorder(time);

        recorder.Start();
        time.AdvanceMs(500);
        recorder.OnKeyDown(KeyCode.VcA);
        time.AdvanceMs(840);

        Assert.Equal(TimeSpan.FromMilliseconds(840), recorder.SinceLastEvent);
    }

    /// <summary>Manually advanced clock. Frequency: 10M ticks/second.</summary>
    private sealed class FakeTime : TimeProvider
    {
        private long timestamp;

        public override long GetTimestamp() => timestamp;

        public void AdvanceMs(int ms) => timestamp += ms * (TimestampFrequency / 1000);
    }
}
