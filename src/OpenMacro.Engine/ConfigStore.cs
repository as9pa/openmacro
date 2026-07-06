using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMacro.Engine;

/// <summary>Saves and loads bindings as human-editable JSON.</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Key codes as names ("VcF11"), not numbers — the file is meant
        // to be hand-editable until the GUI exists.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "openmacro",
            "bindings.json"
        );

    public static void Save(IReadOnlyList<Binding> bindings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(bindings, Options));
    }

    /// <summary>Returns null if no config has been saved yet.</summary>
    public static IReadOnlyList<Binding>? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<IReadOnlyList<Binding>>(File.ReadAllText(path), Options);
    }
}
