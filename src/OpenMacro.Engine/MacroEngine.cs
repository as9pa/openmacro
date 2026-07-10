using SharpHook.Data;

namespace OpenMacro.Engine;

/// <summary>
/// Runs macros for a set of bindings. The hook layer feeds it raw trigger
/// key-downs/ups; the engine owns all playback state and threading.
/// </summary>
public sealed class MacroEngine : IAsyncDisposable
{
    private readonly IInputSink sink;
    private readonly Dictionary<KeyCode, BindingState> bindings;

    // Who has focus, for bindings with an app filter — injected so the engine
    // stays OS-agnostic and tests can fake it. Null means "no filtering
    // available": filtered bindings fire everywhere.
    private readonly Func<string?>? foregroundApp;

    // Hard stop: cancels mid-cycle (dispose/app exit). Soft stop is per-binding
    // StopRequested, which lets the cycle in progress finish (plan: graceful stop).
    private readonly CancellationTokenSource hardStop = new();

    public MacroEngine(
        IInputSink sink,
        IEnumerable<Binding> bindings,
        Func<string?>? foregroundApp = null
    )
    {
        this.sink = sink;
        this.foregroundApp = foregroundApp;
        this.bindings = bindings.ToDictionary(b => b.Trigger, b => new BindingState(b));
    }

    public IReadOnlyCollection<KeyCode> ArmedKeys => bindings.Keys;

    /// <summary>
    /// Called on the hook thread for every real (non-simulated) key-down.
    /// Must stay fast. Returns true if the key is an armed trigger — the
    /// caller should suppress it.
    /// </summary>
    public bool TriggerDown(KeyCode key)
    {
        if (!bindings.TryGetValue(key, out var state))
            return false;

        lock (state)
        {
            // Typematic auto-repeat streams key-downs while held: keep
            // suppressing, but only the first real press acts. An unclaimed
            // hold (wrong app had focus at the press) keeps passing through.
            if (state.TriggerIsDown)
                return true;
            if (state.UnclaimedDown)
                return false;

            // App filter: with another app focused the key must behave like
            // a normal key — no fire, no suppression, and the matching
            // key-up passes through too.
            if (!MatchesForeground(state.Binding))
            {
                state.UnclaimedDown = true;
                return false;
            }

            state.TriggerIsDown = true;

            switch (state.Binding.Mode)
            {
                case PlaybackMode.Once:
                    state.QueuedRuns++;
                    EnsureRunning(state, repeat: false);
                    break;

                case PlaybackMode.WhileHeld:
                    // Re-press during the wind-down cycle cancels the stop.
                    state.StopRequested = false;
                    EnsureRunning(state, repeat: true);
                    break;

                case PlaybackMode.Toggle:
                    if (state.IsRunning)
                        RequestStop(state);
                    else
                        EnsureRunning(state, repeat: true);
                    break;
            }
        }

        return true;
    }

    /// <summary>Hook-thread counterpart for key-up. Returns true if armed.</summary>
    public bool TriggerUp(KeyCode key)
    {
        if (!bindings.TryGetValue(key, out var state))
            return false;

        lock (state)
        {
            // The press passed through (app filter), so its release must too.
            if (state.UnclaimedDown)
            {
                state.UnclaimedDown = false;
                return false;
            }

            state.TriggerIsDown = false;

            // Wake a playback paused at a WaitForRelease step.
            state.ReleaseWaiter?.TrySetResult();
            state.ReleaseWaiter = null;

            if (state.Binding.Mode == PlaybackMode.WhileHeld)
                RequestStop(state);
        }

        return true;
    }

    private bool MatchesForeground(Binding binding)
    {
        if (binding.AppFilter is not { Length: > 0 } filter || foregroundApp is null)
            return true;

        return string.Equals(foregroundApp(), filter, StringComparison.OrdinalIgnoreCase);
    }

    // Caller must hold the state lock. Waking the StopWaiter lets a playback
    // parked at an infinite wait continue to its remaining (cleanup) steps.
    private static void RequestStop(BindingState state)
    {
        state.StopRequested = true;
        state.StopWaiter?.TrySetResult();
        state.StopWaiter = null;
    }

    // Caller must hold the state lock.
    private void EnsureRunning(BindingState state, bool repeat)
    {
        if (state.IsRunning)
            return;

        state.StopRequested = false;
        state.IsRunning = true;
        state.Playback = Task.Run(() => PlayAsync(state, repeat));
    }

    /// <summary>
    /// Plays a macro once, outside any binding — the "Run now" path. Honors
    /// the same hard-stop and never leaves a key or button held down.
    /// </summary>
    public async Task RunOnceAsync(Macro macro)
    {
        var held = new HeldInput();
        try
        {
            await RunCycleAsync(macro, held, state: null);
        }
        catch (OperationCanceledException) { }
        finally
        {
            held.ReleaseAll(sink);
        }
    }

    private async Task PlayAsync(BindingState state, bool repeat)
    {
        // Keys and mouse buttons this playback has pressed but not yet released.
        var held = new HeldInput();

        try
        {
            while (true)
            {
                await RunCycleAsync(state.Binding.Macro, held, state);

                lock (state)
                {
                    var runAgain = repeat ? !state.StopRequested : --state.QueuedRuns > 0;

                    if (!runAgain || hardStop.IsCancellationRequested)
                    {
                        state.IsRunning = false;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (state)
            {
                state.IsRunning = false;
            }
        }
        finally
        {
            // Never leave a key or button held down — whatever stopped us,
            // release leftovers.
            held.ReleaseAll(sink);
        }
    }

    // state is null when the macro runs without a trigger ("Run now") — then
    // WaitForRelease steps have nothing to wait on and complete immediately.
    private async Task RunCycleAsync(Macro macro, HeldInput held, BindingState? state)
    {
        foreach (var macroEvent in macro.Events)
        {
            hardStop.Token.ThrowIfCancellationRequested();

            switch (macroEvent)
            {
                case KeyDownEvent e:
                    sink.KeyDown(e.Key);
                    held.Keys.Add(e.Key);
                    break;

                case KeyUpEvent e:
                    sink.KeyUp(e.Key);
                    held.Keys.Remove(e.Key);
                    break;

                case MouseDownEvent e:
                    sink.MouseDown(e.Button);
                    held.Buttons.Add(e.Button);
                    break;

                case MouseUpEvent e:
                    sink.MouseUp(e.Button);
                    held.Buttons.Remove(e.Button);
                    break;

                case ScrollEvent e:
                    sink.Scroll(e.Direction, e.Clicks);
                    break;

                case TextEvent e:
                    sink.Text(e.Text);
                    break;

                case DelayEvent { Infinite: false } e:
                    await Task.Delay(e.Milliseconds, hardStop.Token);
                    break;

                case DelayEvent: // infinite — park until asked to stop
                {
                    // Without a trigger ("Run now") nothing could ever stop
                    // it, so it completes immediately, like WaitForRelease.
                    if (state is null)
                        break;

                    Task? stopped = null;
                    lock (state)
                    {
                        if (!state.StopRequested)
                        {
                            state.StopWaiter ??= new(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            );
                            stopped = state.StopWaiter.Task;
                        }
                    }

                    if (stopped is not null)
                        await stopped.WaitAsync(hardStop.Token);
                    break;
                }

                case WaitForReleaseEvent:
                {
                    if (state is null)
                        break;

                    Task? released = null;
                    lock (state)
                    {
                        if (state.TriggerIsDown)
                        {
                            // One waiter per pause; TriggerUp completes it.
                            state.ReleaseWaiter ??= new(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            );
                            released = state.ReleaseWaiter.Task;
                        }
                    }

                    if (released is not null)
                        await released.WaitAsync(hardStop.Token);
                    break;
                }
            }
        }
    }

    private sealed class HeldInput
    {
        public HashSet<KeyCode> Keys { get; } = [];
        public HashSet<MouseButton> Buttons { get; } = [];

        public void ReleaseAll(IInputSink sink)
        {
            foreach (var key in Keys)
                sink.KeyUp(key);
            foreach (var button in Buttons)
                sink.MouseUp(button);
        }
    }

    public async ValueTask DisposeAsync()
    {
        hardStop.Cancel();

        var playbacks = bindings
            .Values.Select(s => s.Playback)
            .Where(t => t is not null)
            .Cast<Task>()
            .ToArray();

        await Task.WhenAll(playbacks);
        hardStop.Dispose();
    }

    private sealed class BindingState(Binding binding)
    {
        public Binding Binding { get; } = binding;

        // All mutable state below is guarded by lock(this).
        public bool TriggerIsDown;

        // Physically held, but the press wasn't ours (app filter said no):
        // repeats and the release keep passing through untouched.
        public bool UnclaimedDown;
        public bool StopRequested;
        public bool IsRunning;
        public int QueuedRuns;
        public Task? Playback;

        // Set while playback is paused at a WaitForRelease step; completed
        // (and cleared) by TriggerUp.
        public TaskCompletionSource? ReleaseWaiter;

        // Set while playback is parked at an infinite wait; completed (and
        // cleared) by RequestStop.
        public TaskCompletionSource? StopWaiter;
    }
}
