using System.Text.Json.Serialization;
using SharpHook.Data;

namespace OpenMacro.Engine;

/// <summary>One step in a macro. A macro is an ordered list of these.</summary>
// The "type" discriminator tells System.Text.Json which concrete record a
// JSON object is, and keeps the config file human-editable.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(KeyDownEvent), "keyDown")]
[JsonDerivedType(typeof(KeyUpEvent), "keyUp")]
[JsonDerivedType(typeof(MouseDownEvent), "mouseDown")]
[JsonDerivedType(typeof(MouseUpEvent), "mouseUp")]
[JsonDerivedType(typeof(TextEvent), "text")]
[JsonDerivedType(typeof(DelayEvent), "delay")]
[JsonDerivedType(typeof(WaitForReleaseEvent), "waitForRelease")]
public abstract record MacroEvent;

public sealed record KeyDownEvent(KeyCode Key) : MacroEvent;

public sealed record KeyUpEvent(KeyCode Key) : MacroEvent;

public sealed record MouseDownEvent(MouseButton Button) : MacroEvent;

public sealed record MouseUpEvent(MouseButton Button) : MacroEvent;

public sealed record TextEvent(string Text) : MacroEvent;

/// <summary>
/// Pauses playback until the trigger key is physically released. Splits a
/// macro into an on-press part and an on-release part — e.g. hold G while the
/// trigger is held, release G when it's let go. Completes immediately if the
/// trigger is already up (or when the macro runs without a trigger, e.g.
/// "Run now").
/// </summary>
public sealed record WaitForReleaseEvent : MacroEvent;

public sealed record DelayEvent : MacroEvent
{
    // Every delay is at least 1 ms (plan rule: clamp, don't reject).
    public int Milliseconds { get; }

    public DelayEvent(int milliseconds) => Milliseconds = Math.Max(1, milliseconds);
}
