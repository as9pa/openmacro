using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace OpenMacro.App;

public partial class App : Application
{
    // Two instances would fight over the hook and the config file.
    private static Mutex? instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(initiallyOwned: true, @"Local\openmacro-app", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("openmacro is already running.", "openmacro");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ApplyWindowsAccent();
    }

    /// <summary>
    /// Retints the theme's accent to the user's Windows accent color, so the
    /// app matches the rest of their system. Best-effort: if the registry
    /// value is missing, Theme.xaml's neutral fallback stays.
    /// </summary>
    private void ApplyWindowsAccent()
    {
        try
        {
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is not int raw)
                return;

            // The DWORD is 0xAABBGGRR.
            var value = unchecked((uint)raw);
            var accent = EnsureReadableOnDark(
                Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16))
            );

            // Mutate the existing brushes (they aren't frozen) so every
            // template and StaticResource reference picks up the new color.
            ((SolidColorBrush)Resources["AccentBrush"]).Color = accent;
            var field = (Color)Resources["FieldColor"];
            ((SolidColorBrush)Resources["AccentWashBrush"]).Color = Blend(field, accent, 0.28);
        }
        catch
        {
            // Fallback accent from Theme.xaml stays.
        }
    }

    /// <summary>Very dark accents vanish on the graphite theme — pull them
    /// toward white until they read.</summary>
    private static Color EnsureReadableOnDark(Color c)
    {
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance >= 0.35 ? c : Blend(c, Colors.White, 0.45);
    }

    private static Color Blend(Color from, Color to, double t) =>
        Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t)
        );
}
