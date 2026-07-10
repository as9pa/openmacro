using System.Diagnostics;
using OpenMacro.Engine;
using SharpHook.Data;

namespace OpenMacro.Engine.Tests;

public class MacroEngineTests
{
    private static Binding Bind(PlaybackMode mode, params MacroEvent[] events) =>
        new(KeyCode.VcCapsLock, new Macro("test", events), mode);

    private static readonly MacroEvent[] PressA =
    [
        new KeyDownEvent(KeyCode.VcA),
        new DelayEvent(20),
        new KeyUpEvent(KeyCode.VcA),
    ];

    [Fact]
    public async Task Once_FiresExactlyOncePerPress()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [Bind(PlaybackMode.Once, new TextEvent("a"))]
        );

        Assert.True(engine.TriggerDown(KeyCode.VcCapsLock));
        Assert.True(engine.TriggerUp(KeyCode.VcCapsLock));

        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        await Task.Delay(100); // settle: catch extra fires
        Assert.Equal(["text:a"], sink.Snapshot());
    }

    [Fact]
    public async Task Once_AutoRepeatDoesNotRefire()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [Bind(PlaybackMode.Once, new TextEvent("a"))]
        );

        // Typematic repeat: key-downs stream in with no key-up between them.
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);

        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        await Task.Delay(100);
        Assert.Single(sink.Snapshot());
    }

    [Fact]
    public async Task Once_PressesDuringPlaybackQueue()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [Bind(PlaybackMode.Once, new DelayEvent(50), new TextEvent("a"))]
        );

        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        engine.TriggerDown(KeyCode.VcCapsLock); // lands mid-playback of the first run
        engine.TriggerUp(KeyCode.VcCapsLock);

        await WaitUntilAsync(() => sink.Snapshot().Length >= 2);
        await Task.Delay(100);
        Assert.Equal(2, sink.Snapshot().Length);
    }

    [Fact]
    public async Task WhileHeld_RepeatsUntilReleaseAndFinishesTheCycle()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(sink, [Bind(PlaybackMode.WhileHeld, PressA)]);

        engine.TriggerDown(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 6); // at least 2 full cycles
        engine.TriggerUp(KeyCode.VcCapsLock);

        await WaitUntilAsync(() => Stopped(sink));

        var calls = sink.Snapshot();
        // Graceful stop: the last cycle ran to completion, so the final
        // call is a key-up and downs/ups are balanced — nothing left held.
        Assert.Equal($"up:{KeyCode.VcA}", calls[^1]);
        Assert.Equal(
            calls.Count(c => c.StartsWith("down:")),
            calls.Count(c => c.StartsWith("up:"))
        );
    }

    [Fact]
    public async Task Toggle_SecondPressStops()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(sink, [Bind(PlaybackMode.Toggle, PressA)]);

        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 6);

        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => Stopped(sink));

        var calls = sink.Snapshot();
        Assert.Equal($"up:{KeyCode.VcA}", calls[^1]);
    }

    [Fact]
    public async Task Toggle_CanRestartAfterStopping()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(sink, [Bind(PlaybackMode.Toggle, PressA)]);

        // Start, stop, wait for idle.
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 3);
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => Stopped(sink));
        var afterStop = sink.Snapshot().Length;

        // Third press must start it again (stale stop flags would break this).
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length > afterStop);
    }

    [Fact]
    public async Task HardStop_ReleasesHeldKeys()
    {
        var sink = new RecordingSink();
        var engine = new MacroEngine(
            sink,
            [
                Bind(
                    PlaybackMode.Once,
                    new KeyDownEvent(KeyCode.VcA),
                    new DelayEvent(10_000),
                    new KeyUpEvent(KeyCode.VcA)
                ),
            ]
        );

        engine.TriggerDown(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);

        // Dispose mid-delay: the pending KeyUp never runs, so the engine
        // must release the held key itself.
        await engine.DisposeAsync();

        Assert.Equal($"up:{KeyCode.VcA}", sink.Snapshot()[^1]);
    }

    // The e-drag pattern: hold G while the trigger is held, release it (and
    // tap E) when the trigger is let go.
    private static readonly MacroEvent[] HoldGUntilRelease =
    [
        new KeyDownEvent(KeyCode.VcG),
        new WaitForReleaseEvent(),
        new KeyUpEvent(KeyCode.VcG),
    ];

    [Fact]
    public async Task WaitForRelease_PausesUntilTriggerReleased()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [Bind(PlaybackMode.Once, HoldGUntilRelease)]
        );

        engine.TriggerDown(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        await Task.Delay(100); // settle: the key-up must NOT arrive on its own
        Assert.Equal([$"down:{KeyCode.VcG}"], sink.Snapshot());

        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 2);
        Assert.Equal([$"down:{KeyCode.VcG}", $"up:{KeyCode.VcG}"], sink.Snapshot());
    }

    [Fact]
    public async Task WaitForRelease_CompletesImmediatelyWhenTriggerAlreadyUp()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [Bind(PlaybackMode.Once, HoldGUntilRelease)]
        );

        // A quick tap: the trigger is already up by the time playback reaches
        // the wait step, so the macro must run straight through.
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);

        await WaitUntilAsync(() => sink.Snapshot().Length >= 2);
    }

    [Fact]
    public async Task WaitForRelease_IsSkippedByRunOnce()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(sink, []);

        // "Run now" has no trigger to wait on — the wait must be a no-op, not
        // a hang.
        await engine
            .RunOnceAsync(new Macro("drag", HoldGUntilRelease))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([$"down:{KeyCode.VcG}", $"up:{KeyCode.VcG}"], sink.Snapshot());
    }

    [Fact]
    public async Task WaitForRelease_HardStopWhilePausedReleasesHeldKeys()
    {
        var sink = new RecordingSink();
        var engine = new MacroEngine(sink, [Bind(PlaybackMode.Once, HoldGUntilRelease)]);

        engine.TriggerDown(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);

        // Dispose while paused at the wait step: the scripted KeyUp never
        // runs, so the engine must release G itself.
        await engine.DisposeAsync();

        Assert.Equal($"up:{KeyCode.VcG}", sink.Snapshot()[^1]);
    }

    [Fact]
    public async Task UnarmedKeysAreNotHandled()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [Bind(PlaybackMode.Once, new TextEvent("a"))]
        );

        Assert.False(engine.TriggerDown(KeyCode.VcQ));
        Assert.False(engine.TriggerUp(KeyCode.VcQ));
    }

    private static bool Stopped(RecordingSink sink)
    {
        // "Stopped" = no new output for ~3 cycle lengths.
        var before = sink.Snapshot().Length;
        Thread.Sleep(100);
        return sink.Snapshot().Length == before;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition not met within {timeoutMs} ms");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingSink : IInputSink
    {
        private readonly Lock gate = new();
        private readonly List<string> calls = [];

        public void KeyDown(KeyCode key)
        {
            lock (gate)
            {
                calls.Add($"down:{key}");
            }
        }

        public void KeyUp(KeyCode key)
        {
            lock (gate)
            {
                calls.Add($"up:{key}");
            }
        }

        public void MouseDown(MouseButton button)
        {
            lock (gate)
            {
                calls.Add($"mdown:{button}");
            }
        }

        public void MouseUp(MouseButton button)
        {
            lock (gate)
            {
                calls.Add($"mup:{button}");
            }
        }

        public void Text(string text)
        {
            lock (gate)
            {
                calls.Add($"text:{text}");
            }
        }

        public string[] Snapshot()
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }
}
