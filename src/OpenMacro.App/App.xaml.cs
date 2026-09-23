using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OpenMacro.App;

public partial class App : Application
{
    // Two instances would fight over the hook and the config file.
    private static Mutex? instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(initiallyOwned: true, @"Local\openmacro-app", out var createdNew);
        if (!createdNew && !(e.Args.Contains(UpdateService.RestartArg) && WaitForOldInstance()))
        {
            MessageBox.Show("openmacro is already running.", "openmacro");
            Shutdown();
            return;
        }

        UpdateService.DeleteLeftoverOldExe();
        ThemeManager.Apply(Theme.Default);
        base.OnStartup(e);

        // After the first frame, not before it: the retint is a real resource
        // replacement that every DynamicResource consumer has to pick up, and
        // doing it here proves that end to end.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyWindowsAccent));
    }

    /// <summary>Started by "Restart to update": the old instance is still
    /// shutting down and holds the mutex, so wait for it to let go.</summary>
    private static bool WaitForOldInstance()
    {
        try
        {
            return instanceMutex!.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            return true; // it exited without releasing: ours now all the same
        }
    }

    /// <summary>
    /// Retints the theme's accent to the user's Windows accent color, so the
    /// app matches the rest of their system. Only for a theme that asks for
    /// it, and best-effort: if the registry value is missing, the theme's own
    /// accent stays.
    /// </summary>
    private void ApplyWindowsAccent()
    {
        var theme = ThemeManager.Current;
        if (!theme.FollowsWindowsAccent)
            return;

        try
        {
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is not int raw)
                return;

            // The DWORD is 0xAABBGGRR.
            var accent = ThemeManager.WindowsAccent(unchecked((uint)raw));
            ThemeManager.Apply(
                theme with
                {
                    Accent = accent,
                    AccentWash = ThemeManager.Wash(theme.Surface1, accent),
                }
            );
        }
        catch
        {
            // The theme's own accent stays.
        }
    }
}
