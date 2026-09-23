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
    public async Task Repeat_OnePressPlaysConfiguredNumberOfTimes()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcCapsLock,
            new Macro("burst", [new TextEvent("a")]),
            PlaybackMode.Repeat,
            RepeatCount: 3
        );
        await using var engine = new MacroEngine(sink, [binding]);

        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);

        await WaitUntilAsync(() => sink.Snapshot().Length >= 3);
        await Task.Delay(100); // settle: a 4th run must NOT arrive
        Assert.Equal(["text:a", "text:a", "text:a"], sink.Snapshot());
    }

    [Fact]
    public async Task Repeat_SecondPressStopsTheBatchEarly()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcCapsLock,
            new Macro("long batch", PressA),
            PlaybackMode.Repeat,
            RepeatCount: 1000
        );
        await using var engine = new MacroEngine(sink, [binding]);

        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 6); // a few cycles in

        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => Stopped(sink));

        var calls = sink.Snapshot();
        // Far fewer than 1000 cycles ran, the last one finished cleanly, and
        // nothing is left held.
        Assert.True(calls.Length < 100);
        Assert.Equal($"up:{KeyCode.VcA}", calls[^1]);
        Assert.Equal(
            calls.Count(c => c.StartsWith("down:")),
            calls.Count(c => c.StartsWith("up:"))
        );
    }

    [Fact]
    public async Task Repeat_CanRunAgainAfterTheBatchCompletes()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcCapsLock,
            new Macro("burst", [new TextEvent("a")]),
            PlaybackMode.Repeat,
            RepeatCount: 2
        );
        await using var engine = new MacroEngine(sink, [binding]);

        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 2);
        await WaitUntilAsync(() => Stopped(sink));

        // A fresh press starts a fresh batch of 2 (stale counters would break this).
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 4);
        await Task.Delay(100);
        Assert.Equal(4, sink.Snapshot().Length);
    }

    [Fact]
    public async Task Repeat_CountBelowOnePlaysOnce()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcCapsLock,
            new Macro("burst", [new TextEvent("a")]),
            PlaybackMode.Repeat,
            RepeatCount: 0
        );
        await using var engine = new MacroEngine(sink, [binding]);

        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);

        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        await Task.Delay(100);
        Assert.Equal(["text:a"], sink.Snapshot());
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
    public async Task Scroll_RunOnceEmitsScrollInOrderWithClicks()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(sink, []);

        await engine
            .RunOnceAsync(
                new Macro(
                    "wheel",
                    [
                        new KeyDownEvent(KeyCode.VcA),
                        new ScrollEvent(ScrollDirection.Down, 3),
                        new KeyUpEvent(KeyCode.VcA),
                    ]
                )
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [$"down:{KeyCode.VcA}", "scroll:Down:3", $"up:{KeyCode.VcA}"],
            sink.Snapshot()
        );
    }

    // Parks playback between an on-press and an on-release half until the
    // binding is asked to stop — the tail (key-up) still runs before the end.
    private static readonly MacroEvent[] HoldAUntilStop =
    [
        new KeyDownEvent(KeyCode.VcA),
        new DelayEvent(1, infinite: true),
        new KeyUpEvent(KeyCode.VcA),
    ];

    [Fact]
    public async Task InfiniteWait_WhileHeld_ParksUntilReleaseThenFinishesTheCycle()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [Bind(PlaybackMode.WhileHeld, HoldAUntilStop)]
        );

        engine.TriggerDown(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        await Task.Delay(100); // settle: parked, so the key-up must NOT arrive yet
        Assert.Equal([$"down:{KeyCode.VcA}"], sink.Snapshot());

        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => Stopped(sink));
        Assert.Equal([$"down:{KeyCode.VcA}", $"up:{KeyCode.VcA}"], sink.Snapshot());
    }

    [Fact]
    public async Task InfiniteWait_Toggle_ParksUntilSecondPressThenFinishesTheCycle()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(sink, [Bind(PlaybackMode.Toggle, HoldAUntilStop)]);

        // The first press parks it; the release only clears TriggerIsDown so
        // the second press is seen as a real press, not auto-repeat.
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        await Task.Delay(100); // settle: parked, so the key-up must NOT arrive yet
        Assert.Equal([$"down:{KeyCode.VcA}"], sink.Snapshot());

        // The second press asks it to stop; the tail runs and playback ends.
        // Wait for the exact expected output rather than a fixed quiet window:
        // TriggerUp only completes a TCS created with RunContinuationsAsynchronously,
        // so the tail step (KeyUp) resumes on a thread-pool thread on its own
        // schedule. A quiescence heuristic (no new output for ~N ms) can read
        // "stopped" before that resumption has actually appended "up:A" when the
        // thread pool is under load, e.g. on a busy CI runner.
        engine.TriggerDown(KeyCode.VcCapsLock);
        engine.TriggerUp(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 2);
        Assert.Equal([$"down:{KeyCode.VcA}", $"up:{KeyCode.VcA}"], sink.Snapshot());
    }

    [Fact]
    public async Task InfiniteWait_IsSkippedByRunOnce()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(sink, []);

        // "Run now" has no binding that could stop it — the infinite wait must
        // be a no-op, not a hang, and the steps after it still run.
        await engine
            .RunOnceAsync(new Macro("hold", HoldAUntilStop))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([$"down:{KeyCode.VcA}", $"up:{KeyCode.VcA}"], sink.Snapshot());
    }

    [Fact]
    public async Task InfiniteWait_HardStopWhileParkedReleasesHeldKeys()
    {
        var sink = new RecordingSink();
        var engine = new MacroEngine(sink, [Bind(PlaybackMode.Once, HoldAUntilStop)]);

        engine.TriggerDown(KeyCode.VcCapsLock);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);

        // Dispose while parked at the infinite wait: the scripted KeyUp never
        // runs, so the engine must release A itself.
        await engine.DisposeAsync();

        Assert.Equal($"up:{KeyCode.VcA}", sink.Snapshot()[^1]);
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

    [Fact]
    public async Task AppFilter_FiresWhenForegroundMatchesCaseInsensitively()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcCapsLock,
            new Macro("scoped", [new TextEvent("a")]),
            PlaybackMode.Once,
            AppFilter: "Game"
        );
        await using var engine = new MacroEngine(sink, [binding], () => "game");

        Assert.True(engine.TriggerDown(KeyCode.VcCapsLock));
        Assert.True(engine.TriggerUp(KeyCode.VcCapsLock));

        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        Assert.Equal(["text:a"], sink.Snapshot());
    }

    [Fact]
    public async Task AppFilter_PassesThroughWhenAnotherAppIsFocused()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcCapsLock,
            new Macro("scoped", [new TextEvent("a")]),
            PlaybackMode.Once,
            AppFilter: "game"
        );
        await using var engine = new MacroEngine(sink, [binding], () => "editor");

        // The wrong app has focus: the key must act like a normal key — the
        // press, its auto-repeats, and the release all pass through, and
        // nothing fires.
        Assert.False(engine.TriggerDown(KeyCode.VcCapsLock));
        Assert.False(engine.TriggerDown(KeyCode.VcCapsLock)); // typematic repeat
        Assert.False(engine.TriggerUp(KeyCode.VcCapsLock));

        await Task.Delay(100); // settle: nothing may fire late
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public async Task AppFilter_ReevaluatesOnEachPress()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcCapsLock,
            new Macro("scoped", [new TextEvent("a")]),
            PlaybackMode.Once,
            AppFilter: "game"
        );
        var foreground = "editor";
        await using var engine = new MacroEngine(sink, [binding], () => foreground);

        Assert.False(engine.TriggerDown(KeyCode.VcCapsLock));
        Assert.False(engine.TriggerUp(KeyCode.VcCapsLock));

        foreground = "game"; // focus moved to the right app
        Assert.True(engine.TriggerDown(KeyCode.VcCapsLock));
        Assert.True(engine.TriggerUp(KeyCode.VcCapsLock));

        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        Assert.Equal(["text:a"], sink.Snapshot());
    }

    // ---- mouse-button triggers ----

    // Mouse-trigger counterpart of Bind: Trigger stays VcUndefined and the
    // binding fires on the given mouse button instead.
    private static Binding MouseBind(
        MouseButton button,
        PlaybackMode mode,
        params MacroEvent[] events
    ) => new(KeyCode.VcUndefined, new Macro("test", events), mode, MouseTrigger: button);

    [Fact]
    public async Task MouseTrigger_Once_FiresExactlyOncePerPress()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [MouseBind(MouseButton.Button4, PlaybackMode.Once, new TextEvent("a"))]
        );

        Assert.True(engine.MouseTriggerDown(MouseButton.Button4));
        Assert.True(engine.MouseTriggerUp(MouseButton.Button4));

        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        await Task.Delay(100); // settle: catch extra fires
        Assert.Equal(["text:a"], sink.Snapshot());
    }

    [Fact]
    public async Task MouseTrigger_UnboundButtonIsNotHandled()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [MouseBind(MouseButton.Button4, PlaybackMode.Once, new TextEvent("a"))]
        );

        Assert.False(engine.MouseTriggerDown(MouseButton.Button5));
        Assert.False(engine.MouseTriggerUp(MouseButton.Button5));

        await Task.Delay(100); // settle: nothing may fire
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public async Task MouseTrigger_WhileHeld_RepeatsUntilReleaseAndFinishesTheCycle()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [MouseBind(MouseButton.Button4, PlaybackMode.WhileHeld, PressA)]
        );

        engine.MouseTriggerDown(MouseButton.Button4);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 6); // at least 2 full cycles
        engine.MouseTriggerUp(MouseButton.Button4);

        await WaitUntilAsync(() => Stopped(sink));

        var calls = sink.Snapshot();
        // Graceful stop: the last cycle finished, so the final call is a key-up
        // and downs/ups are balanced — nothing left held.
        Assert.Equal($"up:{KeyCode.VcA}", calls[^1]);
        Assert.Equal(
            calls.Count(c => c.StartsWith("down:")),
            calls.Count(c => c.StartsWith("up:"))
        );
    }

    [Fact]
    public async Task MouseTrigger_AppFilter_PassesThroughWhenAnotherAppIsFocused()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcUndefined,
            new Macro("scoped", [new TextEvent("a")]),
            PlaybackMode.Once,
            AppFilter: "game",
            MouseTrigger: MouseButton.Button4
        );
        await using var engine = new MacroEngine(sink, [binding], () => "editor");

        // Wrong app has focus: the button acts like a normal button — both the
        // press and its release pass through, and nothing fires.
        Assert.False(engine.MouseTriggerDown(MouseButton.Button4));
        Assert.False(engine.MouseTriggerUp(MouseButton.Button4));

        await Task.Delay(100); // settle: nothing may fire late
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public async Task MouseTrigger_AppFilter_FiresWhenForegroundMatches()
    {
        var sink = new RecordingSink();
        var binding = new Binding(
            KeyCode.VcUndefined,
            new Macro("scoped", [new TextEvent("a")]),
            PlaybackMode.Once,
            AppFilter: "game",
            MouseTrigger: MouseButton.Button4
        );
        await using var engine = new MacroEngine(sink, [binding], () => "game");

        Assert.True(engine.MouseTriggerDown(MouseButton.Button4));
        Assert.True(engine.MouseTriggerUp(MouseButton.Button4));

        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        Assert.Equal(["text:a"], sink.Snapshot());
    }

    [Fact]
    public async Task KeyAndMouseTriggers_CoexistAndFireIndependently()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [
                new Binding(
                    KeyCode.VcCapsLock,
                    new Macro("keyed", [new TextEvent("k")]),
                    PlaybackMode.Once
                ),
                MouseBind(MouseButton.Button4, PlaybackMode.Once, new TextEvent("m")),
            ]
        );

        Assert.True(engine.TriggerDown(KeyCode.VcCapsLock));
        Assert.True(engine.TriggerUp(KeyCode.VcCapsLock));
        await WaitUntilAsync(() => sink.Snapshot().Contains("text:k"));

        Assert.True(engine.MouseTriggerDown(MouseButton.Button4));
        Assert.True(engine.MouseTriggerUp(MouseButton.Button4));
        await WaitUntilAsync(() => sink.Snapshot().Contains("text:m"));

        await Task.Delay(100); // settle: exactly one fire each
        var calls = sink.Snapshot();
        Assert.Equal(2, calls.Length);
        Assert.Contains("text:k", calls);
        Assert.Contains("text:m", calls);
    }

    [Fact]
    public async Task MouseTrigger_WaitForRelease_PausesUntilButtonReleased()
    {
        var sink = new RecordingSink();
        await using var engine = new MacroEngine(
            sink,
            [MouseBind(MouseButton.Button4, PlaybackMode.Once, HoldGUntilRelease)]
        );

        engine.MouseTriggerDown(MouseButton.Button4);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 1);
        await Task.Delay(100); // settle: the key-up must NOT arrive on its own
        Assert.Equal([$"down:{KeyCode.VcG}"], sink.Snapshot());

        engine.MouseTriggerUp(MouseButton.Button4);
        await WaitUntilAsync(() => sink.Snapshot().Length >= 2);
        Assert.Equal([$"down:{KeyCode.VcG}", $"up:{KeyCode.VcG}"], sink.Snapshot());
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

        public void Scroll(ScrollDirection direction, int clicks)
        {
            lock (gate)
            {
                calls.Add($"scroll:{direction}:{clicks}");
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
