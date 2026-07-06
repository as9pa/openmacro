using OpenMacro.Engine;
using SharpHook;
using SharpHook.Data;

namespace OpenMacro.App;

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
    private MacroEngine? engine;
    private Action<KeyCode>? captureCallback;

    public bool IsArmed => engine is not null;

    public bool IsRecording => recorder.IsRecording;

    /// <summary>Arms the given bindings. Replaces any previously armed engine.</summary>
    public async Task ArmAsync(IEnumerable<Binding> bindings)
    {
        MacroEngine? old;
        lock (gate)
        {
            old = engine;
            engine = new MacroEngine(sink, bindings);
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

    public void CancelCapture()
    {
        lock (gate)
        {
            captureCallback = null;
            UpdateHook();
        }
    }

    // Caller must hold the gate.
    private void UpdateHook()
    {
        var needed = engine is not null || recorder.IsRecording || captureCallback is not null;

        if (needed && hook is null)
        {
            hook = new SimpleGlobalHook(GlobalHookType.Keyboard, runAsyncOnBackgroundThread: true);
            hook.KeyPressed += OnKeyPressed;
            hook.KeyReleased += OnKeyReleased;
            _ = hook.RunAsync();
        }
        else if (!needed && hook is not null)
        {
            var old = hook;
            hook = null;
            // Never dispose the hook from its own callback thread — deadlock.
            _ = Task.Run(old.Dispose);
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated)
            return;

        // Priority: trigger capture, then recording, then armed macros.
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
        lock (gate)
        {
            captureCallback = null;
            oldEngine = engine;
            engine = null;
            oldHook = hook;
            hook = null;
        }

        oldHook?.Dispose();
        if (oldEngine is not null)
            await oldEngine.DisposeAsync();
    }
}
