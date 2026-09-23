using OpenMacro.Engine;
using SharpHook;
using SharpHook.Data;

const KeyCode RecordKey = KeyCode.VcF10;
const KeyCode PlaybackKey = KeyCode.VcF11;

Binding[] defaults =
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

var store = new ConfigStore();
var loaded = store.LoadAll();
foreach (var error in loaded.Errors)
    Console.WriteLine($"  skipped: {error}");
if (loaded.Migrated > 0)
    Console.WriteLine($"  moved {loaded.Migrated} macros to their own files");
var bindings = new List<Binding>(loaded.Found ? loaded.Bindings : defaults);

var sink = new SharpHookInputSink(new EventSimulator());
var engine = new MacroEngine(sink, bindings);
var recorder = new MacroRecorder();
var recordKeyIsDown = false;

// Keyboard-only, synchronous hook (required for SuppressEvent), on a
// background thread so it can never keep the process alive after exit.
using var hook = new SimpleGlobalHook(GlobalHookType.Keyboard, runAsyncOnBackgroundThread: true);

hook.KeyPressed += (_, e) =>
{
    if (e.IsEventSimulated)
        return;

    if (e.Data.KeyCode == RecordKey)
    {
        e.SuppressEvent = true;
        if (recordKeyIsDown)
            return; // typematic repeat
        recordKeyIsDown = true;
        ToggleRecording();
        return;
    }

    if (recorder.IsRecording)
    {
        // Record the key but let it through — you should see what you type.
        // Macro triggers are deliberately inert while recording.
        recorder.OnKeyDown(e.Data.KeyCode);
        return;
    }

    if (engine.TriggerDown(e.Data.KeyCode))
        e.SuppressEvent = true;
};

hook.KeyReleased += (_, e) =>
{
    if (e.IsEventSimulated)
        return;

    if (e.Data.KeyCode == RecordKey)
    {
        e.SuppressEvent = true;
        recordKeyIsDown = false;
        return;
    }

    if (recorder.IsRecording)
    {
        recorder.OnKeyUp(e.Data.KeyCode);
        return;
    }

    if (engine.TriggerUp(e.Data.KeyCode))
        e.SuppressEvent = true;
};

Console.WriteLine("openmacro — phase 3: recorder");
Console.WriteLine($"  F10 starts/stops recording; the recording binds to F11 (once) and is saved.");
Console.WriteLine($"  config: {store.MacrosDirectory}");
Console.WriteLine("  bindings:");
foreach (var b in bindings)
    Console.WriteLine($"    {b.Trigger,-14} {b.Mode,-10} {b.Macro.Name}");
Console.WriteLine("Armed keys are suppressed while this runs. Press Enter to quit.");

_ = hook.RunAsync();

Console.ReadLine();

hook.Dispose();
await engine.DisposeAsync();
Console.WriteLine("Hook removed. Bye.");

void ToggleRecording()
{
    if (!recorder.IsRecording)
    {
        recorder.Start();
        Console.WriteLine("recording... press F10 again to stop.");
        return;
    }

    var macro = recorder.Stop($"recorded {DateTime.Now:HH:mm:ss}");
    if (macro.Events.Count == 0)
    {
        Console.WriteLine("nothing recorded.");
        return;
    }

    // Saving and the engine swap do I/O and wait on playback tasks —
    // not work for the hook thread.
    _ = Task.Run(async () =>
    {
        bindings.RemoveAll(b => b.Trigger == PlaybackKey);
        bindings.Add(new Binding(PlaybackKey, macro, PlaybackMode.Once));
        store.SaveAll(bindings);

        var old = engine;
        engine = new MacroEngine(sink, bindings);
        await old.DisposeAsync();

        Console.WriteLine($"saved {macro.Events.Count} events -> F11 (once).");
    });
}
