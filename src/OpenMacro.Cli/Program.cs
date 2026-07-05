using System.Threading.Channels;
using SharpHook;
using SharpHook.Data;

const KeyCode TriggerKey = KeyCode.VcCapsLock;
const string MacroText = "gg ez";

// Trigger signals queue here; the engine task drains them off the hook thread.
var triggers = Channel.CreateUnbounded<KeyCode>();

var simulator = new EventSimulator();

// Keyboard-only hook: the app never sees mouse events at all.
// SimpleGlobalHook runs handlers synchronously on the hook thread —
// required for SuppressEvent to work.
// The hook thread must be a background thread: a foreground thread keeps
// the process alive after Main returns (zombie process holding the hook).
using var hook = new SimpleGlobalHook(GlobalHookType.Keyboard, runAsyncOnBackgroundThread: true);

var triggerIsDown = false;

hook.KeyPressed += (_, e) =>
{
    // Injected keystrokes (including our own output) come back through the
    // hook; drop them or a macro containing an armed key re-triggers itself.
    if (e.IsEventSimulated || e.Data.KeyCode != TriggerKey)
        return;

    // Swallow the key so it doesn't also do its normal job (toggling Caps Lock).
    e.SuppressEvent = true;

    // Holding a key streams repeated key-downs (typematic repeat);
    // fire once per real press.
    if (triggerIsDown)
        return;
    triggerIsDown = true;

    // This runs on the hook thread — a slow handler adds system-wide input
    // lag, so just signal the engine and return.
    triggers.Writer.TryWrite(e.Data.KeyCode);
};

hook.KeyReleased += (_, e) =>
{
    if (e.IsEventSimulated || e.Data.KeyCode != TriggerKey)
        return;

    // Suppress the release too, so apps never see an orphaned key-up.
    e.SuppressEvent = true;
    triggerIsDown = false;
};

// The engine: stays alive and waiting, so a trigger wakes it instantly.
var engine = Task.Run(async () =>
{
    await foreach (var _ in triggers.Reader.ReadAllAsync())
    {
        simulator.SimulateTextEntry(MacroText);
    }
});

Console.WriteLine($"openmacro — phase 1. Caps Lock types \"{MacroText}\".");
Console.WriteLine("Caps Lock is suppressed while this runs. Press Enter to quit.");

_ = hook.RunAsync();

Console.ReadLine();

hook.Dispose();
triggers.Writer.Complete();
await engine;

Console.WriteLine("Hook removed. Bye.");
