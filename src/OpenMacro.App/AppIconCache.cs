using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace OpenMacro.App;

/// <summary>
/// The last icon seen for an app-filtered process, kept on disk beside the
/// config files so a macro's badge survives a restart while the filtered app
/// isn't running. One 32 by 32 PNG per process name. Nothing here is load
/// bearing: every failure is a no-op and the caller falls back to the text it
/// showed before there was a cache.
/// </summary>
public static class AppIconCache
{
    // Cheap staleness, so an app that reskins its icon eventually lands here.
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static string Folder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "openmacro",
            "icons"
        );

    /// <summary>Stores the icon for a process name, unless a copy younger
    /// than 30 days is already on disk.</summary>
    public static void Save(string processName, BitmapSource icon)
    {
        if (PathFor(processName) is not { } path)
            return;

        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge)
                return;

            Directory.CreateDirectory(Folder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(icon));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        catch (Exception e)
            when (e
                    is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
                        or ArgumentException
            )
        {
            Debug.WriteLine($"icon cache: couldn't write {processName}: {e.Message}");
        }
    }

    /// <summary>The stored icon for a process name, or null when nothing is
    /// cached for it and when what is cached no longer reads.</summary>
    public static BitmapSource? Load(string processName)
    {
        if (PathFor(processName) is not { } path || !File.Exists(path))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read now, let the file go
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze(); // usable from any thread, no live resource behind it
            return image;
        }
        catch (Exception e)
            when (e
                    is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
                        // a half-written or hand-replaced file
                        or FileFormatException
                        or ArgumentException
            )
        {
            Debug.WriteLine($"icon cache: couldn't read {processName}: {e.Message}");
            return null;
        }
    }

    // The file name is the process name with whatever the file system rejects
    // swapped out; the in-memory key stays the raw name.
    private static string? PathFor(string processName) =>
        string.IsNullOrWhiteSpace(processName)
            ? null
            : Path.Combine(
                Folder,
                string.Join("_", processName.Split(Path.GetInvalidFileNameChars())) + ".png"
            );
}
