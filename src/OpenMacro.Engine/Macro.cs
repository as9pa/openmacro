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
}

public sealed record Binding(KeyCode Trigger, Macro Macro, PlaybackMode Mode);
