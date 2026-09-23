using OpenMacro.Engine;
using SharpHook.Data;

namespace OpenMacro.Engine.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(
        Path.GetTempPath(),
        $"openmacro-test-{Guid.NewGuid():N}"
    );

    private ConfigStore NewStore() => new(dir);

    // The old round-trip tests, run through the per-macro files: save all,
    // then load with a fresh store as the app does on launch.
    private IReadOnlyList<Binding> SaveAndReload(IReadOnlyList<Binding> bindings)
    {
        NewStore().SaveAll(bindings);
        var result = NewStore().LoadAll();
        Assert.Empty(result.Errors);
        return result.Bindings;
    }

    [Fact]
    public void RoundTripsAllEventAndModeKinds()
    {
        Binding[] bindings =
        [
            new(
                KeyCode.VcCapsLock,
                new Macro(
                    "mixed",
                    [
                        new KeyDownEvent(KeyCode.VcLeftShift),
                        new TextEvent("hello"),
                        new DelayEvent(250),
                        new WaitForReleaseEvent(),
                        new KeyUpEvent(KeyCode.VcLeftShift),
                    ]
                ),
                PlaybackMode.Once
            ),
            new(KeyCode.VcF8, new Macro("held", [new TextEvent("x")]), PlaybackMode.WhileHeld),
            new(KeyCode.VcF9, new Macro("toggled", [new DelayEvent(1)]), PlaybackMode.Toggle),
        ];

        var loaded = SaveAndReload(bindings);

        Assert.Equal(bindings.Length, loaded.Count);
        for (var i = 0; i < bindings.Length; i++)
        {
            Assert.Equal(bindings[i].Trigger, loaded[i].Trigger);
            Assert.Equal(bindings[i].Mode, loaded[i].Mode);
            Assert.Equal(bindings[i].Macro.Name, loaded[i].Macro.Name);
            // Element-wise: Macro's record equality is reference-based for
            // the list property, but the events themselves are records.
            Assert.Equal(bindings[i].Macro.Events, loaded[i].Macro.Events);
        }
    }

    [Fact]
    public void RoundTripsScrollAndInfiniteDelay()
    {
        Binding[] bindings =
        [
            new(
                KeyCode.VcCapsLock,
                new Macro(
                    "wheel-and-park",
                    [
                        new ScrollEvent(ScrollDirection.Down, 3),
                        new DelayEvent(500, infinite: true),
                        new DelayEvent(500),
                    ]
                ),
                PlaybackMode.WhileHeld
            ),
        ];

        var loaded = SaveAndReload(bindings);

        var events = loaded[0].Macro.Events;
        Assert.Equal(bindings[0].Macro.Events, events);

        var scroll = Assert.IsType<ScrollEvent>(events[0]);
        Assert.Equal(ScrollDirection.Down, scroll.Direction);
        Assert.Equal(3, scroll.Clicks);

        var infinite = Assert.IsType<DelayEvent>(events[1]);
        Assert.Equal(500, infinite.Milliseconds);
        Assert.True(infinite.Infinite);

        // An old-style plain delay omits Infinite from JSON, so it must load
        // back as non-infinite.
        var plain = Assert.IsType<DelayEvent>(events[2]);
        Assert.False(plain.Infinite);
    }

    [Fact]
    public void RoundTripsAppFilter()
    {
        Binding[] bindings =
        [
            new(
                KeyCode.VcF6,
                new Macro("scoped", [new TextEvent("gg")]),
                PlaybackMode.Once,
                AppFilter: "notepad"
            ),
            new(KeyCode.VcF7, new Macro("global", [new TextEvent("x")]), PlaybackMode.Once),
        ];

        var loaded = SaveAndReload(bindings);

        Assert.Equal("notepad", loaded[0].AppFilter);
        // Pre-filter configs omit the property, so it must load back as null.
        Assert.Null(loaded[1].AppFilter);
    }

    [Fact]
    public void RoundTripsRepeatCount()
    {
        Binding[] bindings =
        [
            new(
                KeyCode.VcF6,
                new Macro("burst", [new TextEvent("a")]),
                PlaybackMode.Repeat,
                RepeatCount: 25
            ),
            new(KeyCode.VcF7, new Macro("plain", [new TextEvent("x")]), PlaybackMode.Once),
        ];

        var loaded = SaveAndReload(bindings);

        Assert.Equal(PlaybackMode.Repeat, loaded[0].Mode);
        Assert.Equal(25, loaded[0].RepeatCount);
        // Pre-repeat configs omit the property, so it must load back as the
        // default of 1.
        Assert.Equal(1, loaded[1].RepeatCount);
    }

    [Fact]
    public void RoundTripsMouseTrigger()
    {
        Binding[] bindings =
        [
            new(
                KeyCode.VcUndefined,
                new Macro("clicky", [new TextEvent("a")]),
                PlaybackMode.Once,
                MouseTrigger: MouseButton.Button4
            ),
            new(KeyCode.VcF7, new Macro("keyed", [new TextEvent("x")]), PlaybackMode.Once),
        ];

        var store = NewStore();
        store.SaveAll(bindings);

        // The button serializes as its readable name, not a number.
        Assert.Contains("\"Button4\"", File.ReadAllText(store.PathFor(bindings[0])!));

        var loaded = SaveAndReload(bindings);

        Assert.Equal(MouseButton.Button4, loaded[0].MouseTrigger);
        // Pre-mouse-trigger configs omit the property, so it must load back null.
        Assert.Null(loaded[1].MouseTrigger);
    }

    [Fact]
    public void SavedFileUsesReadableNames()
    {
        var binding = new Binding(
            KeyCode.VcF11,
            new Macro("m", [new KeyDownEvent(KeyCode.VcA)]),
            PlaybackMode.Once
        );
        var store = NewStore();
        store.SaveBinding(binding);

        var json = File.ReadAllText(store.PathFor(binding)!);
        // Hand-editability: key codes and discriminators as strings, not numbers.
        Assert.Contains("\"VcF11\"", json);
        Assert.Contains("\"keyDown\"", json);
        Assert.Contains("\"Once\"", json);
    }

    [Fact]
    public void LoadReportsNotFoundWhenNoConfigExists()
    {
        var result = NewStore().LoadAll();

        Assert.False(result.Found);
        Assert.Empty(result.Bindings);
        Assert.Empty(result.Errors);
    }

    private static Binding Named(string name, KeyCode trigger = KeyCode.VcF7) =>
        new(trigger, new Macro(name, [new TextEvent(name)]), PlaybackMode.Once);

    [Fact]
    public void PerFileRoundTripKeepsEveryField()
    {
        var binding = new Binding(
            KeyCode.VcF6,
            new Macro("every field", [new TextEvent("a"), new DelayEvent(20)]),
            PlaybackMode.Repeat,
            Enabled: false,
            AppFilter: "notepad",
            RepeatCount: 7,
            MouseTrigger: MouseButton.Button5
        );
        var store = NewStore();
        store.SaveBinding(binding);

        Assert.Equal(Path.Combine(dir, "macros", "every-field.json"), store.PathFor(binding));
        var json = File.ReadAllText(store.PathFor(binding)!);
        // The id heads the file, ahead of the fields it identifies.
        Assert.Matches("""^\{\s*"Id": "[0-9a-f]{8}",""", json);
        Assert.Matches("^[0-9a-f]{8}$", binding.Id);

        var loaded = Assert.Single(NewStore().LoadAll().Bindings);
        Assert.Equal(binding.Id, loaded.Id);
        Assert.Equal(binding.Trigger, loaded.Trigger);
        Assert.Equal(binding.Mode, loaded.Mode);
        Assert.Equal(binding.Enabled, loaded.Enabled);
        Assert.Equal(binding.AppFilter, loaded.AppFilter);
        Assert.Equal(binding.RepeatCount, loaded.RepeatCount);
        Assert.Equal(binding.MouseTrigger, loaded.MouseTrigger);
        Assert.Equal(binding.Macro.Name, loaded.Macro.Name);
        Assert.Equal(binding.Macro.Events, loaded.Macro.Events);
    }

    [Fact]
    public void LoadFollowsOrderFileAndAppendsUnlistedFiles()
    {
        Binding a = Named("a"),
            b = Named("b"),
            c = Named("c"),
            d = Named("d");
        var store = NewStore();
        foreach (var binding in new[] { a, b, c, d })
            store.SaveBinding(binding);

        // order.json lists c then a, plus an id with no file; b and d are
        // unlisted and follow in file-name order.
        File.WriteAllText(store.OrderPath, $"[\"{c.Id}\", \"deadbeef\", \"{a.Id}\"]");

        var result = NewStore().LoadAll();

        Assert.Empty(result.Errors);
        Assert.Equal([c.Id, a.Id, b.Id, d.Id], result.Bindings.Select(x => x.Id));
    }

    [Fact]
    public void SaveOrderWritesOnlyTheOrder()
    {
        Binding a = Named("a"),
            b = Named("b");
        var store = NewStore();
        store.SaveAll([a, b]);
        var aWritten = File.GetLastWriteTimeUtc(store.PathFor(a)!);

        store.SaveOrder([b, a]);

        Assert.Equal(aWritten, File.GetLastWriteTimeUtc(store.PathFor(a)!));
        Assert.Equal([b.Id, a.Id], NewStore().LoadAll().Bindings.Select(x => x.Id));
    }

    [Fact]
    public void CorruptFileIsSkippedAndReported()
    {
        Binding good = Named("good"),
            other = Named("other");
        var store = NewStore();
        store.SaveAll([good, other]);
        File.WriteAllText(Path.Combine(store.MacrosDirectory, "wasd.json"), "{ not json");

        var result = NewStore().LoadAll();

        Assert.Equal([good.Id, other.Id], result.Bindings.Select(x => x.Id));
        Assert.Equal(["wasd.json could not be read"], result.Errors);
    }

    [Fact]
    public void MissingIdIsAssignedAndWrittenBack()
    {
        var store = NewStore();
        Directory.CreateDirectory(store.MacrosDirectory);
        File.WriteAllText(
            Path.Combine(store.MacrosDirectory, "old.json"),
            """{ "Trigger": "VcF7", "Macro": { "Name": "old", "Events": [] }, "Mode": "Once" }"""
        );

        var first = Assert.Single(NewStore().LoadAll().Bindings);
        var second = Assert.Single(NewStore().LoadAll().Bindings);

        Assert.Matches("^[0-9a-f]{8}$", first.Id);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void MigrationSplitsBindingsJsonAndBackupGoesOnNextCleanLoad()
    {
        // The pre-id single-file format, as the old ConfigStore wrote it.
        Directory.CreateDirectory(dir);
        var legacyPath = Path.Combine(dir, "bindings.json");
        File.WriteAllText(
            legacyPath,
            """
            [
              { "Trigger": "VcF6", "Macro": { "Name": "wasd", "Events": [ { "type": "text", "Text": "w" } ] }, "Mode": "Once", "AppFilter": "game" },
              { "Trigger": "VcF7", "Macro": { "Name": "wasd", "Events": [] }, "Mode": "Repeat", "RepeatCount": 3 },
              { "Trigger": "VcUndefined", "Macro": { "Name": "", "Events": [] }, "Mode": "Toggle", "Enabled": false, "MouseTrigger": "Button4" }
            ]
            """
        );

        var store = NewStore();
        var migrated = store.LoadAll();

        Assert.Equal(3, migrated.Migrated);
        Assert.Empty(migrated.Errors);
        Assert.Equal(["wasd", "wasd", ""], migrated.Bindings.Select(b => b.Macro.Name));
        Assert.Equal("game", migrated.Bindings[0].AppFilter);
        Assert.Equal(3, migrated.Bindings[1].RepeatCount);
        Assert.Equal(MouseButton.Button4, migrated.Bindings[2].MouseTrigger);
        Assert.Equal(3, migrated.Bindings.Select(b => b.Id).Distinct().Count());
        Assert.Equal(
            ["macro.json", "wasd-2.json", "wasd.json"],
            Directory.GetFiles(store.MacrosDirectory).Select(Path.GetFileName).Order()
        );
        Assert.True(File.Exists(store.OrderPath));
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(store.BackupPath));

        var reloaded = NewStore().LoadAll();

        Assert.Equal(0, reloaded.Migrated);
        Assert.Equal(migrated.Bindings.Select(b => b.Id), reloaded.Bindings.Select(b => b.Id));
        Assert.False(File.Exists(store.BackupPath));
    }

    [Fact]
    public void BackupStaysWhileAFileFailsToLoad()
    {
        var store = NewStore();
        store.SaveAll([Named("a")]);
        File.WriteAllText(store.BackupPath, "[]");
        File.WriteAllText(Path.Combine(store.MacrosDirectory, "broken.json"), "[");

        NewStore().LoadAll();

        Assert.True(File.Exists(store.BackupPath));
    }

    [Fact]
    public void FolderWinsOverBindingsJsonWhenBothExist()
    {
        var store = NewStore();
        store.SaveAll([Named("kept")]);
        File.WriteAllText(store.LegacyPath, "[]");

        var result = NewStore().LoadAll();

        Assert.Equal(0, result.Migrated);
        Assert.Equal("kept", Assert.Single(result.Bindings).Macro.Name);
        Assert.True(File.Exists(store.LegacyPath));
    }

    [Fact]
    public void SlugClashesGetSuffixes()
    {
        Binding first = Named("My Macro"),
            second = Named("my macro"),
            third = Named("My:Macro?"),
            blank = Named("  ");
        var store = NewStore();
        store.SaveAll([first, second, third, blank]);

        Assert.Equal("my-macro.json", Path.GetFileName(store.PathFor(first)));
        Assert.Equal("my-macro-2.json", Path.GetFileName(store.PathFor(second)));
        Assert.Equal("my-macro-3.json", Path.GetFileName(store.PathFor(third)));
        Assert.Equal("macro.json", Path.GetFileName(store.PathFor(blank)));
    }

    [Fact]
    public void RenameMovesTheFile()
    {
        var original = Named("before");
        var store = NewStore();
        store.SaveBinding(original);
        var oldPath = store.PathFor(original)!;

        var renamed = original with { Macro = original.Macro with { Name = "After Rename" } };
        store.SaveBinding(renamed);

        Assert.False(File.Exists(oldPath));
        Assert.Equal("after-rename.json", Path.GetFileName(store.PathFor(renamed)));
        Assert.Equal(
            ["after-rename.json"],
            Directory.GetFiles(store.MacrosDirectory).Select(Path.GetFileName)
        );
        var loaded = Assert.Single(NewStore().LoadAll().Bindings);
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal("After Rename", loaded.Macro.Name);
    }

    [Fact]
    public void DeleteThenRestoreKeepsTheId()
    {
        Binding a = Named("a"),
            b = Named("b");
        var store = NewStore();
        store.SaveAll([a, b]);

        store.DeleteBinding(a);
        Assert.Single(NewStore().LoadAll().Bindings);

        store.SaveBinding(a);
        store.SaveOrder([a, b]);
        Assert.Equal([a.Id, b.Id], NewStore().LoadAll().Bindings.Select(x => x.Id));
    }

    [Fact]
    public void SaveAllRemovesFilesOfDroppedBindings()
    {
        Binding a = Named("a"),
            b = Named("b");
        var store = NewStore();
        store.SaveAll([a, b]);

        store.SaveAll([b]);

        Assert.Equal(
            ["b.json"],
            Directory.GetFiles(store.MacrosDirectory).Select(Path.GetFileName)
        );
    }

    [Fact]
    public void WithKeepsIdAndNewBindingsGetFreshOnes()
    {
        var a = Named("a");

        Assert.Equal(a.Id, (a with { Enabled = false }).Id);
        Assert.NotEqual(a.Id, Named("a").Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
