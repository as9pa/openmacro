using OpenMacro.Engine;
using SharpHook.Data;

namespace OpenMacro.Engine.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string path = Path.Combine(
        Path.GetTempPath(),
        $"openmacro-test-{Guid.NewGuid():N}",
        "bindings.json"
    );

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

        ConfigStore.Save(bindings, path);
        var loaded = ConfigStore.Load(path);

        Assert.NotNull(loaded);
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

        ConfigStore.Save(bindings, path);
        var loaded = ConfigStore.Load(path);

        Assert.NotNull(loaded);
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

        ConfigStore.Save(bindings, path);
        var loaded = ConfigStore.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("notepad", loaded[0].AppFilter);
        // Pre-filter configs omit the property, so it must load back as null.
        Assert.Null(loaded[1].AppFilter);
    }

    [Fact]
    public void SavedFileUsesReadableNames()
    {
        ConfigStore.Save(
            [
                new Binding(
                    KeyCode.VcF11,
                    new Macro("m", [new KeyDownEvent(KeyCode.VcA)]),
                    PlaybackMode.Once
                ),
            ],
            path
        );

        var json = File.ReadAllText(path);
        // Hand-editability: key codes and discriminators as strings, not numbers.
        Assert.Contains("\"VcF11\"", json);
        Assert.Contains("\"keyDown\"", json);
        Assert.Contains("\"Once\"", json);
    }

    [Fact]
    public void LoadReturnsNullWhenNoConfigExists()
    {
        Assert.Null(ConfigStore.Load(path));
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
