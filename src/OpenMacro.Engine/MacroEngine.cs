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

    // Hard stop: cancels mid-cycle (dispose/app exit). Soft stop is per-binding
    // StopRequested, which lets the cycle in progress finish (plan: graceful stop).
    private readonly CancellationTokenSource hardStop = new();

    public MacroEngine(IInputSink sink, IEnumerable<Binding> bindings)
    {
        this.sink = sink;
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
            // suppressing, but only the first real press acts.
            if (state.TriggerIsDown)
                return true;
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
                        state.StopRequested = true;
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
            state.TriggerIsDown = false;

            if (state.Binding.Mode == PlaybackMode.WhileHeld)
                state.StopRequested = true;
        }

        return true;
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

    private async Task PlayAsync(BindingState state, bool repeat)
    {
        // Keys this playback has pressed but not yet released.
        var held = new HashSet<KeyCode>();

        try
        {
            while (true)
            {
                await RunCycleAsync(state.Binding.Macro, held);

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
            // Never leave a key held down — whatever stopped us, release leftovers.
            foreach (var key in held)
                sink.KeyUp(key);
        }
    }

    private async Task RunCycleAsync(Macro macro, HashSet<KeyCode> held)
    {
        foreach (var macroEvent in macro.Events)
        {
            hardStop.Token.ThrowIfCancellationRequested();

            switch (macroEvent)
            {
                case KeyDownEvent e:
                    sink.KeyDown(e.Key);
                    held.Add(e.Key);
                    break;

                case KeyUpEvent e:
                    sink.KeyUp(e.Key);
                    held.Remove(e.Key);
                    break;

                case TextEvent e:
                    sink.Text(e.Text);
                    break;

                case DelayEvent e:
                    await Task.Delay(e.Milliseconds, hardStop.Token);
                    break;
            }
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
        public bool StopRequested;
        public bool IsRunning;
        public int QueuedRuns;
        public Task? Playback;
    }
}
