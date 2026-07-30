using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        nint handle,
        uint flags,
        StringBuilder buffer,
        ref uint size
    );

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    /// Full path of a process's executable, or null. Process.MainModule needs
    /// access rights that elevated and anti-cheat-protected processes (games!)
    /// refuse; the limited-information query is built to still be answered.
    /// </summary>
    public static string? ExecutablePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == 0)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
