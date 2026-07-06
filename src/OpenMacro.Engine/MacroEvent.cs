using SharpHook.Data;

namespace OpenMacro.Engine;

/// <summary>One step in a macro. A macro is an ordered list of these.</summary>
public abstract record MacroEvent;

public sealed record KeyDownEvent(KeyCode Key) : MacroEvent;

public sealed record KeyUpEvent(KeyCode Key) : MacroEvent;

public sealed record TextEvent(string Text) : MacroEvent;

public sealed record DelayEvent : MacroEvent
{
    // Every delay is at least 1 ms (plan rule: clamp, don't reject).
    public int Milliseconds { get; }

    public DelayEvent(int milliseconds) => Milliseconds = Math.Max(1, milliseconds);
}
