using System.IO;
using System.Text.Json;

namespace OpenMacro.App;

// Key/modifier names are System.Windows.Input enum names ("F6",
// "Control, Shift") so the file stays hand-editable, like bindings.json.
// CheckForUpdates gates the launch and six-hourly GitHub release checks.
public sealed record AppSettings(
    string? ArmHotkeyKey = null,
    string? ArmHotkeyModifiers = null,
    bool CheckForUpdates = true
);

/// <summary>
/// App-level settings (not macros — those are ConfigStore's per-macro files).
/// A separate file so the bindings schema stays a plain array.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "openmacro",
            "settings.json"
        );

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A hand-edit gone wrong shouldn't stop the app from opening.
            return new AppSettings();
        }
    }
}
