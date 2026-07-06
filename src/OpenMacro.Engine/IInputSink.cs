using SharpHook;
using SharpHook.Data;

namespace OpenMacro.Engine;

/// <summary>
/// Where playback output goes. The engine depends on this instead of a concrete
/// simulator so tests can record output instead of injecting real input.
/// </summary>
public interface IInputSink
{
    void KeyDown(KeyCode key);
    void KeyUp(KeyCode key);
    void Text(string text);
}

/// <summary>Sends output as real OS input via SharpHook's simulator.</summary>
public sealed class SharpHookInputSink(IEventSimulator simulator) : IInputSink
{
    public void KeyDown(KeyCode key) => simulator.SimulateKeyPress(key);

    public void KeyUp(KeyCode key) => simulator.SimulateKeyRelease(key);

    public void Text(string text) => simulator.SimulateTextEntry(text);
}
