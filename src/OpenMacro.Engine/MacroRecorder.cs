using SharpHook.Data;

namespace OpenMacro.Engine;

/// <summary>
/// Builds a Macro from live key events, inserting the real gaps between them
/// as editable DelayEvents. Time comes from a TimeProvider so tests can
/// control it. Not thread-safe: all calls are expected from the hook thread.
/// </summary>
public sealed class MacroRecorder(TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly List<MacroEvent> events = [];
    private long lastTimestamp;

    public bool IsRecording { get; private set; }

    /// <summary>
    /// Raised on the recording (hook) thread for every event appended, in
    /// the order Stop returns them: the gap's DelayEvent first, then the
    /// input event it precedes. Handlers must not block or touch UI objects.
    /// </summary>
    public event Action<MacroEvent>? StepRecorded;

    /// <summary>
    /// Time since the last recorded event, measured on the recorder's clock.
    /// Safe to read from any thread; meaningless before the first event.
    /// </summary>
    public TimeSpan SinceLastEvent => time.GetElapsedTime(Volatile.Read(ref lastTimestamp));

    public void Start()
    {
        events.Clear();
        lastTimestamp = 0;
        IsRecording = true;
    }

    /// <summary>Stops recording and returns what was captured (possibly empty).</summary>
    public Macro Stop(string name)
    {
        IsRecording = false;
        return new Macro(name, events.ToArray());
    }

    public void OnKeyDown(KeyCode key) => Add(new KeyDownEvent(key));

    public void OnKeyUp(KeyCode key) => Add(new KeyUpEvent(key));

    public void OnMouseDown(MouseButton button) => Add(new MouseDownEvent(button));

    public void OnMouseUp(MouseButton button) => Add(new MouseUpEvent(button));

    // One event per hook notification: a long scroll records as several
    // single-click steps, each editable afterwards.
    public void OnScroll(ScrollDirection direction) => Add(new ScrollEvent(direction));

    private void Add(MacroEvent macroEvent)
    {
        if (!IsRecording)
            return;

        var now = time.GetTimestamp();

        // The gap since the previous event becomes an editable delay.
        // No delay before the first event: playback starts immediately.
        if (events.Count > 0)
        {
            var gap = (int)time.GetElapsedTime(lastTimestamp, now).TotalMilliseconds;
            var delay = new DelayEvent(gap); // DelayEvent clamps to >= 1 ms
            events.Add(delay);
            StepRecorded?.Invoke(delay);
        }

        Volatile.Write(ref lastTimestamp, now);
        events.Add(macroEvent);
        StepRecorded?.Invoke(macroEvent);
    }
}
