using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMacro.Engine;

/// <summary>What <see cref="ConfigStore.LoadAll"/> found: the bindings in
/// sidebar order, one line per file it had to skip ("wasd.json could not be
/// read"), and how many macros it just moved out of an old single-file
/// bindings.json (0 when it didn't). <see cref="Found"/> is false only when
/// nothing has ever been saved.</summary>
public sealed record LoadResult(
    IReadOnlyList<Binding> Bindings,
    IReadOnlyList<string> Errors,
    int Migrated,
    bool Found
);

/// <summary>Saves and loads bindings as human-editable JSON: one file per
/// macro under <c>macros\</c>, named after the macro, plus <c>order.json</c>
/// holding the ids in sidebar order. The id inside a file is its identity;
/// the file name is cosmetic and follows the macro's name.</summary>
public sealed class ConfigStore(string? directory = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Key codes as names ("VcF11"), not numbers — the files are meant
        // to stay hand-editable.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "openmacro"
        );

    public string Directory { get; } = directory ?? DefaultDirectory;

    public string MacrosDirectory => Path.Combine(Directory, "macros");

    public string OrderPath => Path.Combine(Directory, "order.json");

    /// <summary>The single file every binding lived in before per-macro
    /// files. Read once to migrate, then renamed to <see cref="BackupPath"/>.</summary>
    public string LegacyPath => Path.Combine(Directory, "bindings.json");

    public string BackupPath => LegacyPath + ".bak";

    // Which file holds each id, as of the last load or write. Renames and
    // deletes go through it, since the name on disk may be an older name's.
    private readonly Dictionary<string, string> fileById = [];

    /// <summary>The file holding <paramref name="binding"/>, or null if it
    /// hasn't been written yet.</summary>
    public string? PathFor(Binding binding) => fileById.GetValueOrDefault(binding.Id);

    /// <summary>Loads every macro in sidebar order. A file that won't parse
    /// is skipped and named in <see cref="LoadResult.Errors"/> rather than
    /// failing the whole load. On the first load after an upgrade this moves
    /// bindings.json into per-macro files; on a later clean load it removes
    /// the bindings.json.bak that move left behind.</summary>
    public LoadResult LoadAll()
    {
        fileById.Clear();

        // The folder wins whenever it exists: bindings.json is only ever
        // read to create it.
        if (!System.IO.Directory.Exists(MacrosDirectory))
            return File.Exists(LegacyPath) ? Migrate() : new LoadResult([], [], 0, Found: false);

        var errors = new List<string>();

        List<string> order = [];
        if (File.Exists(OrderPath))
        {
            try
            {
                order =
                    JsonSerializer.Deserialize<List<string>>(File.ReadAllText(OrderPath), Options)
                    ?? [];
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                errors.Add($"{Path.GetFileName(OrderPath)} could not be read");
            }
        }

        var byId = new Dictionary<string, Binding>();
        var byFileName = new List<string>();
        foreach (var file in MacroFiles(MacrosDirectory))
        {
            var name = Path.GetFileName(file);
            Binding? binding;
            bool hasId;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                hasId =
                    doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(nameof(Binding.Id), out _);
                binding = doc.RootElement.Deserialize<Binding>(Options);
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                errors.Add($"{name} could not be read");
                continue;
            }

            // Parses, but not as a macro (for example "{}" or a bare array).
            if (binding?.Macro?.Events is null)
            {
                errors.Add($"{name} could not be read");
                continue;
            }

            // No id yet (hand-written, or pre-id), or a hand-copied file
            // sharing another's: give it its own and write it back so it
            // keeps it from now on.
            if (!hasId || string.IsNullOrWhiteSpace(binding.Id) || byId.ContainsKey(binding.Id))
            {
                binding = binding with { Id = Binding.NewId() };
                try
                {
                    WriteAtomic(file, Serialize(binding));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{name} could not be updated with its id");
                }
            }

            byId[binding.Id] = binding;
            fileById[binding.Id] = file;
            byFileName.Add(binding.Id);
        }

        // order.json first, skipping ids whose file is gone; then any file
        // it doesn't list, in file-name order.
        var ids = order.Where(byId.ContainsKey).Distinct().ToList();
        ids.AddRange(byFileName.Except(ids));

        if (errors.Count == 0 && File.Exists(BackupPath))
        {
            try
            {
                File.Delete(BackupPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Harmless to leave; the next clean load tries again.
            }
        }

        return new LoadResult([.. ids.Select(id => byId[id])], errors, 0, Found: true);
    }

    /// <summary>Writes one macro's file, and only that file. A changed name
    /// renames the file to match: the old file is moved to the new name and
    /// then overwritten, so exactly one file holds the id at every
    /// step.</summary>
    public void SaveBinding(Binding binding)
    {
        System.IO.Directory.CreateDirectory(MacrosDirectory);

        var slug = Slug(binding.Macro.Name);
        var current = PathFor(binding);
        if (current is not null && !File.Exists(current))
            current = null;

        var target =
            current is not null && IsNameFor(Path.GetFileNameWithoutExtension(current), slug)
                ? current
                : UniquePath(MacrosDirectory, slug, except: current);

        if (current is not null && !PathsEqual(current, target))
            File.Move(current, target);

        WriteAtomic(target, Serialize(binding));
        fileById[binding.Id] = target;
    }

    /// <summary>Removes one macro's file.</summary>
    public void DeleteBinding(Binding binding)
    {
        if (fileById.Remove(binding.Id, out var file) && File.Exists(file))
            File.Delete(file);
    }

    /// <summary>Writes order.json, and only that: the ids in sidebar
    /// order.</summary>
    public void SaveOrder(IEnumerable<Binding> bindings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WriteAtomic(
            OrderPath,
            JsonSerializer.Serialize(bindings.Select(b => b.Id).ToList(), Options)
        );
    }

    /// <summary>For callers that changed many things at once: writes every
    /// binding and the order, and removes the file of any macro this store
    /// knows about that is no longer in <paramref name="bindings"/>. Files it
    /// never loaded (a corrupt one, say) are left alone.</summary>
    public void SaveAll(IReadOnlyList<Binding> bindings)
    {
        foreach (var binding in bindings)
            SaveBinding(binding);
        SaveOrder(bindings);

        var keep = bindings.Select(b => b.Id).ToHashSet();
        foreach (var (id, file) in fileById.Where(kv => !keep.Contains(kv.Key)).ToList())
        {
            fileById.Remove(id);
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    /// <summary>A file-name-safe form of a macro name: lowercase, with spaces
    /// and characters Windows won't take in a file name as "-", trimmed, and
    /// "macro" when nothing is left.</summary>
    public static string Slug(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? "")
            .ToLowerInvariant()
            .Select(c => char.IsWhiteSpace(c) || invalid.Contains(c) ? '-' : c)
            .ToArray();
        var slug = new string(chars).Trim('-', '.', ' ');
        if (slug.Length > 60)
            slug = slug[..60].TrimEnd('-', '.', ' ');
        if (slug.Length == 0)
            return "macro";

        // Device names Windows refuses as file names, extension or not.
        return ReservedNames.Contains(slug) ? $"{slug}-macro" : slug;
    }

    private static readonly HashSet<string> ReservedNames =
    [
        "con",
        "prn",
        "aux",
        "nul",
        .. Enumerable.Range(1, 9).Select(n => $"com{n}"),
        .. Enumerable.Range(1, 9).Select(n => $"lpt{n}"),
    ];

    /// <summary>Splits bindings.json into per-macro files. The files are
    /// built in a scratch folder and moved into place in one step, so an
    /// interrupted move leaves no half-filled macros folder to win over
    /// bindings.json next time.</summary>
    private LoadResult Migrate()
    {
        List<Binding> legacy;
        try
        {
            legacy =
                JsonSerializer.Deserialize<List<Binding>>(File.ReadAllText(LegacyPath), Options)
                ?? [];
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return new LoadResult(
                [],
                [$"{Path.GetFileName(LegacyPath)} could not be read"],
                0,
                Found: true
            );
        }

        // Pre-id bindings got a fresh id each as they parsed; this only
        // guards a hand-edited file that repeats one.
        var seen = new HashSet<string>();
        var bindings = legacy
            .Select(b => seen.Add(b.Id) ? b : b with { Id = Binding.NewId() })
            .ToList();

        var staging = Path.Combine(Directory, $"macros.{Guid.NewGuid():N}.tmp");
        System.IO.Directory.CreateDirectory(staging);
        var names = new Dictionary<string, string>();
        foreach (var binding in bindings)
        {
            var file = UniquePath(staging, Slug(binding.Macro.Name), except: null);
            WriteAtomic(file, Serialize(binding));
            names[binding.Id] = Path.GetFileName(file);
        }

        SaveOrder(bindings);
        System.IO.Directory.Move(staging, MacrosDirectory);
        foreach (var (id, name) in names)
            fileById[id] = Path.Combine(MacrosDirectory, name);

        File.Move(LegacyPath, BackupPath, overwrite: true);
        return new LoadResult(bindings, [], bindings.Count, Found: true);
    }

    private static IEnumerable<string> MacroFiles(string dir) =>
        System
            .IO.Directory.GetFiles(dir, "*.json")
            .Where(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="stem"/> is <paramref name="slug"/>
    /// or a clash suffix of it ("wasd-2"), so the file already carries the
    /// macro's name and needs no rename.</summary>
    private static bool IsNameFor(string stem, string slug)
    {
        if (stem.Equals(slug, StringComparison.OrdinalIgnoreCase))
            return true;

        return stem.StartsWith(slug + "-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(stem[(slug.Length + 1)..], out var n)
            && n >= 2
            && stem[(slug.Length + 1)..] == n.ToString();
    }

    /// <summary>The first of <c>slug.json</c>, <c>slug-2.json</c>, ... that
    /// no file takes, other than <paramref name="except"/> (the macro's own
    /// file).</summary>
    private static string UniquePath(string dir, string slug, string? except)
    {
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(dir, (n == 1 ? slug : $"{slug}-{n}") + ".json");
            if (!File.Exists(path) || (except is not null && PathsEqual(path, except)))
                return path;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Serialize(Binding binding) => JsonSerializer.Serialize(binding, Options);

    /// <summary>Writes to a temp file beside <paramref name="path"/> and moves
    /// it over, so a crash mid-write never leaves a half-written file.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsReadFailure(Exception e) =>
        e
            is JsonException
                or NotSupportedException
                or InvalidOperationException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException;
}
