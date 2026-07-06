using OpenMacro.Engine;
using SharpHook;
using SharpHook.Data;

Binding[] bindings =
[
    new(KeyCode.VcCapsLock, new Macro("type gg ez", [new TextEvent("gg ez")]), PlaybackMode.Once),
    new(
        KeyCode.VcF8,
        new Macro("x while held", [new TextEvent("x"), new DelayEvent(150)]),
        PlaybackMode.WhileHeld
    ),
    new(
        KeyCode.VcF9,
        new Macro("spam toggle", [new TextEvent("spam "), new DelayEvent(500)]),
        PlaybackMode.Toggle
    ),
];

await using var engine = new MacroEngine(new SharpHookInputSink(new EventSimulator()), bindings);

// Keyboard-only, synchronous hook (required for SuppressEvent), on a
// background thread so it can never keep the process alive after exit.
using var hook = new SimpleGlobalHook(GlobalHookType.Keyboard, runAsyncOnBackgroundThread: true);

hook.KeyPressed += (_, e) =>
{
    // Injected keystrokes (including our own output) come back through the
    // hook; drop them or a macro containing an armed key re-triggers itself.
    if (e.IsEventSimulated)
        return;

    // Armed trigger: swallow the key so it doesn't also do its normal job.
    // The engine handles auto-repeat and all playback state off this thread.
    if (engine.TriggerDown(e.Data.KeyCode))
        e.SuppressEvent = true;
};

hook.KeyReleased += (_, e) =>
{
    if (e.IsEventSimulated)
        return;

    if (engine.TriggerUp(e.Data.KeyCode))
        e.SuppressEvent = true;
};

Console.WriteLine("openmacro — phase 2: playback modes");
Console.WriteLine(
    """
      Caps Lock   once         types "gg ez"
      F8          while held   types x every 150 ms
      F9          toggle       types "spam " every 500 ms until pressed again
    Armed keys are suppressed while this runs. Press Enter to quit.
    """
);

_ = hook.RunAsync();

Console.ReadLine();

hook.Dispose();

// engine is disposed by `await using` after this: hard-stops playback and
// releases anything still held.

Console.WriteLine("Hook removed. Bye.");
