using OpenMacro.Engine;
using SharpHook;
using SharpHook.Data;

namespace OpenMacro.App;

/// <summary>What a one-shot "press anything" capture saw: a key, a mouse
/// button, or a wheel scroll.</summary>
public abstract record CapturedInput
{
    public sealed record Key(KeyCode Code) : CapturedInput;

    public sealed record Mouse(MouseButton Button) : CapturedInput;

    public sealed record Scroll(ScrollDirection Direction) : CapturedInput;
}

/// <summary>
/// Owns the global hook and everything that listens to it: the macro engine,
/// the recorder, and one-shot trigger capture. The hook exists only while at
/// least one of those is active (the plan's "no hook unless a macro is
/// enabled" rule) — idle app means nothing is watching the keyboard.
/// </summary>
public sealed class HookService : IAsyncDisposable
{
    private readonly SharpHookInputSink sink = new(new EventSimulator());
    private readonly MacroRecorder recorder = new();
    private readonly object gate = new();

    private SimpleGlobalHook? hook;
    private GlobalHookType hookType;
    private MacroEngine? engine;
    private Action<KeyCode>? captureCallback;
    private Action<CapturedInput>? inputCaptureCallback;
    private Action<CapturedInput>? triggerCaptureCallback;

    // libuiohook has one process-wide run loop, so hooks must start and stop
    // strictly one at a time. Dispose() only signals the loop to stop and
    // returns before it has actually exited, so starting a replacement right
    // away (e.g. switching to the mouse hook when recording begins) races the
    // dying loop and its RunAsync returns at once, delivering nothing. hookOps
    // serialises every stop/start off the callback thread (disposing from it
    // deadlocks); hookLoop completes when the live hook's loop has fully exited.
    private Task hookOps = Task.CompletedTask;
    private Task hookLoop = Task.CompletedTask;

    public bool IsArmed => engine is not null;

    public bool IsRecording => recorder.IsRecording;

    // "Run now" playbacks in flight; they bypass the armed engine.
    private int oneShotRuns;

    /// <summary>True while a macro is playing, armed or "Run now".</summary>
    public bool IsPlaying
    {
        get
        {
            if (Volatile.Read(ref oneShotRuns) > 0)
                return true;
            lock (gate)
                return engine?.IsPlaying == true;
        }
    }

    /// <summary>
    /// Screen-pixel test for "is this point on our own window", set by the
    /// UI. Clicks there while recording are operating the recorder (e.g.
    /// pressing Stop), not part of the macro, so they are not captured.
    /// Called on the hook thread — must not touch UI objects.
    /// </summary>
    public Func<short, short, bool>? IsOwnWindowPoint { get; set; }

    /// <summary>
    /// Plays a macro once, immediately — the "Run now" path. No hook or
    /// arming involved: output is injected directly.
    /// </summary>
    public async Task RunMacroOnceAsync(Macro macro)
    {
        var oneShot = new MacroEngine(sink, []);
        Interlocked.Increment(ref oneShotRuns);
        try
        {
            await oneShot.RunOnceAsync(macro);
        }
        finally
        {
            Interlocked.Decrement(ref oneShotRuns);
            await oneShot.DisposeAsync();
        }
    }

    /// <summary>Arms the given bindings. Replaces any previously armed engine.</summary>
    public async Task ArmAsync(IEnumerable<Binding> bindings)
    {
        MacroEngine? old;
        lock (gate)
        {
            old = engine;
            engine = new MacroEngine(sink, bindings, ForegroundApp.ProcessName);
            UpdateHook();
        }

        if (old is not null)
            await old.DisposeAsync();
    }

    public async Task DisarmAsync()
    {
        MacroEngine? old;
        lock (gate)
        {
            old = engine;
            engine = null;
            UpdateHook();
        }

        if (old is not null)
            await old.DisposeAsync();
    }

    public void StartRecording()
    {
        lock (gate)
        {
            recorder.Start();
            UpdateHook();
        }
    }

    public Macro StopRecording(string name)
    {
        lock (gate)
        {
            var macro = recorder.Stop(name);
            UpdateHook();
            return macro;
        }
    }

    /// <summary>
    /// The next real key-down is suppressed and reported to
    /// <paramref name="onCaptured"/> (on the hook thread — marshal to the UI
    /// thread in the callback).
    /// </summary>
    public void CaptureNextKey(Action<KeyCode> onCaptured)
    {
        lock (gate)
        {
            captureCallback = onCaptured;
            UpdateHook();
        }
    }

    /// <summary>
    /// Like <see cref="CaptureNextKey"/> but the next mouse button or wheel
    /// scroll counts too (this is the "Add input" flow, so the mouse hook is
    /// up while it waits). The captured event is suppressed.
    /// </summary>
    public void CaptureNextInput(Action<CapturedInput> onCaptured)
    {
        lock (gate)
        {
            inputCaptureCallback = onCaptured;
            UpdateHook();
        }
    }

    /// <summary>
    /// One-shot capture for setting a binding's trigger: the next key or mouse
    /// button is claimed, suppressed, and reported. Left click (Button1) is the
    /// exception — it passes through so the overlay scrim's click-to-cancel
    /// still works, and it's deliberately not allowed as a trigger. Wheel
    /// scrolls are ignored (a scroll can't be a trigger). The mouse hook is up
    /// while this waits.
    /// </summary>
    public void CaptureNextTrigger(Action<CapturedInput> onCaptured)
    {
        lock (gate)
        {
            triggerCaptureCallback = onCaptured;
            UpdateHook();
        }
    }

    public void CancelCapture()
    {
        lock (gate)
        {
            captureCallback = null;
            inputCaptureCallback = null;
            triggerCaptureCallback = null;
            UpdateHook();
        }
    }

    // Caller must hold the gate.
    private void UpdateHook()
    {
        var needed =
            engine is not null
            || recorder.IsRecording
            || captureCallback is not null
            || inputCaptureCallback is not null
            || triggerCaptureCallback is not null;

        // Mouse events matter while recording, waiting on an "Add input" or
        // trigger capture, or when the armed engine has a mouse-button trigger
        // to watch; the rest of the time the narrower keyboard-only hook keeps
        // the trust story simple.
        var neededType =
            recorder.IsRecording
            || inputCaptureCallback is not null
            || triggerCaptureCallback is not null
            || engine?.HasMouseTriggers == true
                ? GlobalHookType.All
                : GlobalHookType.Keyboard;

        if (hook is not null && (!needed || hookType != neededType))
        {
            var old = hook;
            var oldLoop = hookLoop;
            hook = null;
            // Stop, then block the chain until the old loop has really exited so
            // the next hook can install cleanly.
            hookOps = hookOps.ContinueWith(
                _ =>
                {
                    old.Dispose();
                    oldLoop.Wait();
                },
                TaskScheduler.Default
            );
        }

        if (needed && hook is null)
        {
            var next = new SimpleGlobalHook(neededType, runAsyncOnBackgroundThread: true);
            next.KeyPressed += OnKeyPressed;
            next.KeyReleased += OnKeyReleased;
            next.MousePressed += OnMousePressed;
            next.MouseReleased += OnMouseReleased;
            next.MouseWheel += OnMouseWheel;
            hook = next;
            hookType = neededType;

            // Start only after any queued stop has finished, and expose the run
            // loop's completion so a later replacement can wait on it here.
            var loop = new TaskCompletionSource();
            hookLoop = loop.Task;
            hookOps = hookOps.ContinueWith(
                _ =>
                {
                    _ = next.RunAsync()
                        .ContinueWith(done => loop.TrySetResult(), TaskScheduler.Default);
                },
                TaskScheduler.Default
            );
        }
    }

    // One-shot claim of a pending "Add input" capture; drops the hook back
    // to keyboard-only (or off) once claimed.
    private Action<CapturedInput>? TakeInputCapture()
    {
        lock (gate)
        {
            var capture = inputCaptureCallback;
            if (capture is not null)
            {
                inputCaptureCallback = null;
                UpdateHook();
            }
            return capture;
        }
    }

    // One-shot claim of a pending trigger capture; drops the hook back to
    // keyboard-only (or off) once claimed.
    private Action<CapturedInput>? TakeTriggerCapture()
    {
        lock (gate)
        {
            var capture = triggerCaptureCallback;
            if (capture is not null)
            {
                triggerCaptureCallback = null;
                UpdateHook();
            }
            return capture;
        }
    }

    private void OnMousePressed(object? sender, MouseHookEventArgs e)
    {
        if (e.IsEventSimulated)
            return;

        // Input capture takes clicks anywhere — including on our own window,
        // which the overlay is covering.
        if (TakeInputCapture() is { } capture)
        {
            e.SuppressEvent = true;
            capture(new CapturedInput.Mouse(e.Data.Button));
            return;
        }

        // Trigger capture claims any button except left click, which passes
        // through untouched so the overlay scrim's click-to-cancel handles it
        // (and left click is not an allowed trigger anyway).
        if (e.Data.Button != MouseButton.Button1 && TakeTriggerCapture() is { } triggerCapture)
        {
            e.SuppressEvent = true;
            triggerCapture(new CapturedInput.Mouse(e.Data.Button));
            return;
        }

        if (recorder.IsRecording)
        {
            // Own-window clicks operate the recorder (e.g. Stop), not the
            // macro; armed triggers are inert while recording.
            if (IsOwnWindowPoint?.Invoke(e.Data.X, e.Data.Y) != true)
                recorder.OnMouseDown(e.Data.Button);
            return;
        }

        if (engine?.MouseTriggerDown(e.Data.Button) == true)
            e.SuppressEvent = true;
    }

    private void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
    {
        // Tilt-wheel (horizontal) scrolling isn't a macro step.
        if (e.IsEventSimulated || e.Data.Direction != MouseWheelScrollDirection.Vertical)
            return;

        var direction = e.Data.Rotation > 0 ? ScrollDirection.Up : ScrollDirection.Down;

        if (TakeInputCapture() is { } capture)
        {
            e.SuppressEvent = true;
            capture(new CapturedInput.Scroll(direction));
            return;
        }

        if (!recorder.IsRecording)
            return;

        if (IsOwnWindowPoint?.Invoke(e.Data.X, e.Data.Y) == true)
            return;

        recorder.OnScroll(direction);
    }

    private void OnMouseReleased(object? sender, MouseHookEventArgs e)
    {
        if (e.IsEventSimulated)
            return;

        if (recorder.IsRecording)
        {
            if (IsOwnWindowPoint?.Invoke(e.Data.X, e.Data.Y) != true)
                recorder.OnMouseUp(e.Data.Button);
            return;
        }

        if (engine?.MouseTriggerUp(e.Data.Button) == true)
            e.SuppressEvent = true;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated)
            return;

        // Priority: key capture (step replacement), then trigger capture, then
        // input capture, then recording, then armed macros.
        Action<KeyCode>? capture;
        lock (gate)
        {
            capture = captureCallback;
            if (capture is not null)
            {
                captureCallback = null;
                UpdateHook();
            }
        }

        if (capture is not null)
        {
            e.SuppressEvent = true;
            capture(e.Data.KeyCode);
            return;
        }

        if (TakeTriggerCapture() is { } triggerCapture)
        {
            e.SuppressEvent = true;
            triggerCapture(new CapturedInput.Key(e.Data.KeyCode));
            return;
        }

        if (TakeInputCapture() is { } inputCapture)
        {
            e.SuppressEvent = true;
            inputCapture(new CapturedInput.Key(e.Data.KeyCode));
            return;
        }

        if (recorder.IsRecording)
        {
            // Record and pass through; armed triggers are inert while recording.
            recorder.OnKeyDown(e.Data.KeyCode);
            return;
        }

        if (engine?.TriggerDown(e.Data.KeyCode) == true)
            e.SuppressEvent = true;
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated)
            return;

        if (recorder.IsRecording)
        {
            recorder.OnKeyUp(e.Data.KeyCode);
            return;
        }

        if (engine?.TriggerUp(e.Data.KeyCode) == true)
            e.SuppressEvent = true;
    }

    public async ValueTask DisposeAsync()
    {
        MacroEngine? oldEngine;
        SimpleGlobalHook? oldHook;
        Task ops,
            loop;
        lock (gate)
        {
            captureCallback = null;
            inputCaptureCallback = null;
            triggerCaptureCallback = null;
            oldEngine = engine;
            engine = null;
            oldHook = hook;
            hook = null;
            ops = hookOps;
            loop = hookLoop;
        }

        // Let any queued start/stop settle before tearing the live hook down,
        // then wait for its loop to exit so the process leaves no hook running.
        await ops;
        oldHook?.Dispose();
        await loop;
        if (oldEngine is not null)
            await oldEngine.DisposeAsync();
    }
}
