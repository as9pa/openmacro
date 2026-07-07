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
    void MouseDown(MouseButton button);
    void MouseUp(MouseButton button);
    void Text(string text);
}

/// <summary>Sends output as real OS input via SharpHook's simulator.</summary>
public sealed class SharpHookInputSink(IEventSimulator simulator) : IInputSink
{
    public void KeyDown(KeyCode key) => simulator.SimulateKeyPress(key);

    public void KeyUp(KeyCode key) => simulator.SimulateKeyRelease(key);

    public void MouseDown(MouseButton button) => simulator.SimulateMousePress(button);

    public void MouseUp(MouseButton button) => simulator.SimulateMouseRelease(button);

    public void Text(string text) => simulator.SimulateTextEntry(text);
}
