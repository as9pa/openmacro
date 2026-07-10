using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenMacro.App;

/// <summary>
/// Which process owns the foreground window — the input for app-filtered
/// bindings. Called on the hook thread, and only when an armed trigger that
/// has a filter is pressed, so the cost stays off the common path.
/// </summary>
public static class ForegroundApp
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>Process name without extension ("notepad"), or null when
    /// there's no usable foreground window.</summary>
    public static string? ProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
            return null;

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return null;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null; // the window's process exited under us
        }
    }
}
