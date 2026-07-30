using System.Text.Json.Serialization;
using SharpHook.Data;

namespace OpenMacro.Engine;

public sealed record Macro(string Name, IReadOnlyList<MacroEvent> Events);

public enum PlaybackMode
{
    /// <summary>Each press runs the macro once; presses during playback queue up.</summary>
    Once,

    /// <summary>Repeats while the trigger is held; the cycle in progress finishes after release.</summary>
    WhileHeld,

    /// <summary>Press starts repeating, press again stops (after the cycle in progress).</summary>
    Toggle,

    /// <summary>Press runs the macro <see cref="Binding.RepeatCount"/> times;
    /// press again stops early (after the cycle in progress).</summary>
    Repeat,
}

// Enabled defaults to true so configs saved before the flag existed load as
// enabled. AppFilter is a process name without extension ("notepad"),
// case-insensitive; null fires anywhere. When the filter doesn't match the
// foreground app, the trigger key acts as a normal key (passes through).
// RepeatCount only applies in Repeat mode; anything below 1 plays once.
// MouseTrigger fires the binding on a mouse button instead of a key — when
// it's set, Trigger is VcUndefined and the engine routes by whichever one is
// set. Old configs omit the property, so it loads as null (a key trigger).
public sealed record Binding(
    KeyCode Trigger,
    Macro Macro,
    PlaybackMode Mode,
    bool Enabled = true,
    string? AppFilter = null,
    int RepeatCount = 1,
    MouseButton? MouseTrigger = null
)
{
    /// <summary>True when this binding has any trigger set — a key or a mouse
    /// button — so it can be armed.</summary>
    [JsonIgnore]
    public bool HasTrigger => MouseTrigger is not null || Trigger != KeyCode.VcUndefined;
}
