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
